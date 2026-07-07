import { useState, useEffect } from 'react';
import { Shield, ExternalLink, CheckCircle2, AlertCircle, ChevronRight, Bookmark, Copy, RefreshCw } from 'lucide-react';

const SERVER_URL = import.meta.env.VITE_SERVER_URL || 'http://localhost:5000';
const DIARY_URL  = 'https://dnevnik.egov66.ru';

type Step = 'welcome' | 'get-bookmarklet' | 'polling' | 'success' | 'error';

export default function App() {
  const [step, setStep] = useState<Step>('welcome');
  const [tgUserId, setTgUserId] = useState('');
  const [tgFirstName, setTgFirstName] = useState('');
  
  const [code, setCode] = useState('');
  const [bookmarkletJs, setBookmarkletJs] = useState('');
  const [errorMsg, setErrorMsg] = useState('');
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    try {
      const tg = (window as any).Telegram?.WebApp;
      if (tg) {
        tg.ready();
        tg.expand();
        const user = tg.initDataUnsafe?.user;
        if (user?.id)         setTgUserId(String(user.id));
        if (user?.first_name) setTgFirstName(user.first_name);
      } else {
        setTgUserId('123456789');
        setTgFirstName('Тест');
      }
    } catch { setTgUserId('123456789'); setTgFirstName('Тест'); }
  }, []);

  // Poll for completion
  useEffect(() => {
    if (step !== 'polling' || !code) return;
    const interval = setInterval(async () => {
      try {
        const res = await fetch(`${SERVER_URL}/api/miniapp/poll-auth/${code}`);
        if (res.ok) {
          const data = await res.json();
          if (data.done) {
            clearInterval(interval);
            setStep('success');
            setTimeout(() => (window as any).Telegram?.WebApp?.close?.(), 3000);
          }
        }
      } catch { /* ignore poll errors */ }
    }, 2000);
    return () => clearInterval(interval);
  }, [step, code]);

  const handleStart = async () => {
    setStep('polling'); // show loading state briefly
    try {
      const res = await fetch(`${SERVER_URL}/api/miniapp/create-code`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ TelegramUserId: tgUserId }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setCode(data.code);
      setBookmarkletJs(data.bookmarkletJs);
      setStep('get-bookmarklet');
    } catch (e: any) {
      setErrorMsg('Не удалось получить код. ' + e.message);
      setStep('error');
    }
  };

  const copyToClipboard = () => {
    navigator.clipboard.writeText(bookmarkletJs).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    });
  };

  const openDiary = () => {
    setStep('polling');
    window.open(DIARY_URL, '_blank');
  };

  // ─── WELCOME ─────────────────────────────────────────────────────────────
  if (step === 'welcome') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle"><Shield size={38} className="logo-icon" /></div>
        <h1 className="app-title">Mirage Diary</h1>
        <p className="app-sub">
          {tgFirstName ? `Привет, ${tgFirstName}! ` : ''}
          Авторизация в один клик.
        </p>
      </div>

      <div className="steps-card">
        <div className="step-row"><span className="step-badge">1</span><span>Сохраните спец-ссылку</span></div>
        <div className="step-row"><span className="step-badge">2</span><span>Зайдите в дневник через Госуслуги</span></div>
        <div className="step-row"><span className="step-badge">3</span><span>Откройте сохраненную ссылку</span></div>
      </div>

      <button className="btn-primary" onClick={handleStart}>
        Начать <ChevronRight size={18} />
      </button>
    </div>
  );

  // ─── GET BOOKMARKLET ─────────────────────────────────────────────────────
  if (step === 'get-bookmarklet') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Bookmark size={32} className="logo-icon" /></div>
        <h1 className="app-title">Скопируйте скрипт</h1>
        <p className="app-sub">Это безопасно — код только отправляет ваш токен боту</p>
      </div>

      <div className="instruction-card">
        <p className="instruction-label">1. Скопируйте этот код:</p>
        <button className="copy-btn" onClick={copyToClipboard}>
          {copied ? <><CheckCircle2 size={16} /> Скопировано</> : <><Copy size={16} /> Скопировать</>}
        </button>
      </div>

      <div className="instruction-card mt-2">
        <p className="instruction-label">2. Создайте закладку в браузере:</p>
        <ol className="instruction-list">
          <li>Создайте любую закладку</li>
          <li>Измените её <strong>Адрес (URL)</strong></li>
          <li>Вставьте туда скопированный скрипт</li>
        </ol>
      </div>

      <button className="btn-primary btn-green mt-4" onClick={openDiary}>
        3. Открыть дневник <ExternalLink size={16} />
      </button>
    </div>
  );

  // ─── POLLING ─────────────────────────────────────────────────────────────
  if (step === 'polling') return (
    <div className="screen screen-center">
      <div className="spinner-wrap">
        <div className="spinner-ring" />
      </div>
      <p className="sync-title">Ожидаю авторизации...</p>
      <p className="sync-sub mt-2">
        1. Войдите в дневник<br/>
        2. Откройте сохранённую закладку
      </p>
      <div className="code-box mt-4">Код сессии: {code}</div>
      <button className="btn-ghost mt-4" onClick={() => setStep('get-bookmarklet')}>
        Показать инструкцию
      </button>
    </div>
  );

  // ─── SUCCESS ─────────────────────────────────────────────────────────────
  if (step === 'success') return (
    <div className="screen screen-center">
      <div className="success-circle">
        <CheckCircle2 size={52} className="success-icon" />
      </div>
      <h2 className="success-title">Готово!</h2>
      <p className="success-sub mt-2">Дневник успешно подключён. Окно закроется автоматически.</p>
    </div>
  );

  // ─── ERROR ───────────────────────────────────────────────────────────────
  if (step === 'error') return (
    <div className="screen screen-center">
      <div className="error-circle">
        <AlertCircle size={52} className="error-icon" />
      </div>
      <h2 className="error-title">Ошибка</h2>
      <p className="error-sub mt-2">{errorMsg}</p>
      <button className="btn-primary" onClick={handleStart}>
        <RefreshCw size={16} /> Попробовать снова
      </button>
    </div>
  );

  return null;
}
