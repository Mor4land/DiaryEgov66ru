(function() {
    if (window.__apiLoggerInjected) return;
    window.__apiLoggerInjected = true;

    function sendToExtension(data) {
        window.postMessage({ type: 'API_LOGGER_ENTRY', payload: data }, '*');
    }

    // --- INTERCEPT FETCH ---
    const originalFetch = window.fetch;
    window.fetch = async function(...args) {
        const startTime = new Date().toISOString();
        let url = typeof args[0] === 'string' ? args[0] : (args[0] && args[0].url ? args[0].url : String(args[0]));
        let options = args[1] || {};
        let method = options.method || (args[0] && args[0].method) || 'GET';
        let reqBody = options.body || (args[0] && args[0].body) || null;

        let entry = {
            id: Math.random().toString(36).substring(2, 9),
            type: 'fetch',
            timestamp: startTime,
            url: url,
            method: method,
            requestHeaders: options.headers || {},
            requestBody: safeParseJson(reqBody)
        };

        try {
            const response = await originalFetch.apply(this, args);
            const clone = response.clone();
            const resText = await clone.text();

            entry.status = response.status;
            entry.statusText = response.statusText;
            entry.responseHeaders = Object.fromEntries(response.headers.entries());
            entry.responseBody = safeParseJson(resText);

            sendToExtension(entry);
            return response;
        } catch (error) {
            entry.error = error.message;
            sendToExtension(entry);
            throw error;
        }
    };

    // --- INTERCEPT XHR ---
    const XHR = XMLHttpRequest.prototype;
    const open = XHR.open;
    const send = XHR.send;

    XHR.open = function(method, url) {
        this._apiLog = {
            id: Math.random().toString(36).substring(2, 9),
            type: 'xhr',
            timestamp: new Date().toISOString(),
            url: url,
            method: method,
            requestHeaders: {}
        };
        const setRequestHeader = this.setRequestHeader;
        this.setRequestHeader = function(header, value) {
            if (this._apiLog) {
                this._apiLog.requestHeaders[header] = value;
            }
            return setRequestHeader.apply(this, arguments);
        };
        return open.apply(this, arguments);
    };

    XHR.send = function(postData) {
        if (this._apiLog) {
            this._apiLog.requestBody = safeParseJson(postData);

            this.addEventListener('load', function() {
                let entry = this._apiLog;
                entry.status = this.status;
                entry.statusText = this.statusText;
                entry.responseBody = safeParseJson(this.responseText);
                sendToExtension(entry);
            });

            this.addEventListener('error', function() {
                let entry = this._apiLog;
                entry.error = 'Network Error';
                sendToExtension(entry);
            });
        }
        return send.apply(this, arguments);
    };

    function safeParseJson(data) {
        if (typeof data !== 'string') return data;
        try {
            return JSON.parse(data);
        } catch (e) {
            return data;
        }
    }
})();
