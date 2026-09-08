(function() {
    // Inject script into page context
    const script = document.createElement('script');
    script.src = chrome.runtime.getURL('injected.js');
    (document.head || document.documentElement).appendChild(script);
    script.onload = function() {
        script.remove();
    };

    let capturedLogs = [];

    // Listen for events from injected script
    window.addEventListener('message', function(event) {
        if (event.data && event.data.type === 'API_LOGGER_ENTRY') {
            capturedLogs.push(event.data.payload);
            updateUiCounter();
        }
    });

    // Create Floating UI Overlay
    function createUI() {
        if (document.getElementById('api-sniffer-widget')) return;

        const widget = document.createElement('div');
        widget.id = 'api-sniffer-widget';
        widget.style.cssText = `
            position: fixed;
            bottom: 20px;
            right: 20px;
            z-index: 999999;
            background: rgba(24, 24, 27, 0.95);
            color: #ffffff;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            border-radius: 12px;
            padding: 12px 16px;
            box-shadow: 0 10px 25px rgba(0,0,0,0.5);
            border: 1px solid rgba(255,255,255,0.15);
            display: flex;
            align-items: center;
            gap: 12px;
            font-size: 13px;
            backdrop-filter: blur(10px);
        `;

        widget.innerHTML = `
            <div style="display:flex; align-items:center; gap:6px; font-weight:600;">
                <span style="display:inline-block; width:10px; height:10px; background:#22c55e; border-radius:50%; box-shadow:0 0 8px #22c55e;"></span>
                <span>API Sniffer:</span>
                <span id="api-sniffer-count" style="background:#3f3f46; padding:2px 8px; border-radius:10px;">0</span>
            </div>
            <button id="api-sniffer-download" style="
                background: #2563eb;
                color: white;
                border: none;
                padding: 6px 12px;
                border-radius: 6px;
                cursor: pointer;
                font-weight: 500;
                font-size: 12px;
                transition: background 0.2s;
            ">📥 Скачать JSON</button>
            <button id="api-sniffer-clear" style="
                background: #3f3f46;
                color: #e4e4e7;
                border: none;
                padding: 6px 10px;
                border-radius: 6px;
                cursor: pointer;
                font-weight: 500;
                font-size: 12px;
            ">🗑️</button>
        `;

        document.body.appendChild(widget);

        document.getElementById('api-sniffer-download').addEventListener('click', downloadLogs);
        document.getElementById('api-sniffer-clear').addEventListener('click', clearLogs);
    }

    function updateUiCounter() {
        const countEl = document.getElementById('api-sniffer-count');
        if (countEl) {
            countEl.innerText = capturedLogs.length;
        }
    }

    function clearLogs() {
        capturedLogs = [];
        updateUiCounter();
    }

    function downloadLogs() {
        if (capturedLogs.length === 0) {
            alert('Запросы ещё не перехвачены!');
            return;
        }

        const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(capturedLogs, null, 2));
        const downloadAnchor = document.createElement('a');
        const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
        
        downloadAnchor.setAttribute("href", dataStr);
        downloadAnchor.setAttribute("download", `api_dump_${window.location.hostname}_${timestamp}.json`);
        document.body.appendChild(downloadAnchor);
        downloadAnchor.click();
        downloadAnchor.remove();
    }

    // Wait for DOM to append UI
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', createUI);
    } else {
        createUI();
    }
})();
