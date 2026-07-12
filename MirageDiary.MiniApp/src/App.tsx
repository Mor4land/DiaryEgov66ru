import { useState, useEffect } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import {
  Fingerprint,
  ShieldCheck,
  Send,
  ArrowRight,
} from 'lucide-react';

declare global {
  interface Window {
    Telegram?: {
      WebApp?: {
        ready: () => void;
        expand: () => void;
        close: () => void;
        initDataUnsafe?: {
          user?: { id: number; first_name: string };
        };
      };
    };
  }
}

const SERVER_URL = import.meta.env.VITE_SERVER_URL || 'http://localhost:5000';

type Step =
  | 'loading'
  | 'welcome'
  | 'login'
  | 'polling'
  | 'otp'
  | 'captcha'
  | 'success'
  | 'error'
  | 'already_authorized';

const screenVariants = {
  initial: (direction: 'forward' | 'backward') => ({
    x: direction === 'forward' ? '100%' : '-100%',
    opacity: 0,
  }),
  animate: {
    x: 0,
    opacity: 1,
    transition: {
      type: 'spring' as const,
      stiffness: 320,
      damping: 30,
      mass: 0.8,
    },
  },
  exit: (direction: 'forward' | 'backward') => ({
    x: direction === 'forward' ? '-100%' : '100%',
    opacity: 0,
    transition: {
      type: 'spring' as const,
      stiffness: 320,
      damping: 30,
      mass: 0.8,
    },
  }),
};

// Framer Motion Stagger Animation Variants for Welcome screen
const staggerContainer = {
  animate: {
    transition: {
      staggerChildren: 0.06,
    },
  },
};

const staggerItem = {
  initial: { y: 15, opacity: 0 },
  animate: {
    y: 0,
    opacity: 1,
    transition: {
      type: 'spring' as const,
      stiffness: 300,
      damping: 24,
    },
  },
};

const buttonSpring = {
  hover: { scale: 1.01, y: -1 },
  tap: { scale: 0.975 },
};

export default function App() {
  const [step, setStep] = useState<Step>('loading');
  const [direction, setDirection] = useState<'forward' | 'backward'>('forward');
  const [tgUserId, setTgUserId] = useState('');
  const [tgFirstName, setTgFirstName] = useState('');
  const [login, setLogin] = useState('');
  const [password, setPassword] = useState('');
  const [sessionId, setSessionId] = useState('');
  const [userInput, setUserInput] = useState('');
  const [captchaImg, setCaptchaImg] = useState('');
  const [errorMsg, setErrorMsg] = useState('');
  const [progressText, setProgressText] = useState('Открываем безопасное соединение...');
  const [progressStep, setProgressStep] = useState(1);

  useEffect(() => {
    try {
      const tg = window.Telegram?.WebApp;
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
    } catch {
      setTgUserId('123456789');
      setTgFirstName('Тест');
    }
  }, []);

  useEffect(() => {
    if (!tgUserId) return;
    const checkAuth = async () => {
      try {
        const res = await fetch(`${SERVER_URL}/api/miniapp/auth/check/${tgUserId}`, {
          headers: { 'ngrok-skip-browser-warning': 'true' },
        });
        if (res.ok) {
          const data = await res.json();
          if (data.isAuthorized) {
            setStep('already_authorized');
            return;
          }
        }
      } catch (e: unknown) {
        console.error(e);
      }
      setStep('welcome');
      setProgressStep(1);
    };
    checkAuth();
  }, [tgUserId]);

  useEffect(() => {
    if (step !== 'polling' || !sessionId) return;

    let pct = 0;
    const progTimer = setInterval(() => {
      pct = Math.min(pct + 1, 95);
      if (pct === 3) setProgressText('Запускаем защищённый браузер...');
      else if (pct === 8) setProgressText('Открываем страницу авторизации...');
      else if (pct === 15) setProgressText('Вводим данные для входа...');
      else if (pct === 25) setProgressText('Ожидаем ответ от Госуслуг...');
      else if (pct === 40) setProgressText('Проверяем двухфакторную защиту...');
      else if (pct === 55) setProgressText('Подключаемся к Дневнику...');
      else if (pct === 70) setProgressText('Получаем токен доступа...');
      else if (pct === 85) setProgressText('Финализируем авторизацию...');
    }, 300);

    const pollTimer = setInterval(async () => {
      try {
        const res = await fetch(`${SERVER_URL}/api/miniapp/auth/status/${sessionId}`, {
          headers: { 'ngrok-skip-browser-warning': 'true' },
        });
        if (!res.ok) return;
        const data = await res.json();
        const clear = () => {
          clearInterval(pollTimer);
          clearInterval(progTimer);
        };

        if (data.status === 'Success') {
          clear();
          setProgressText('Готово!');
          setProgressStep(3);
          setTimeout(() => {
            setDirection('forward');
            setStep('success');
          }, 600);
          setTimeout(() => window.Telegram?.WebApp?.close?.(), 4000);
        } else if (data.status === 'RequiresOtp') {
          clear();
          setUserInput('');
          setProgressStep(2);
          setDirection('forward');
          setStep('otp');
        } else if (data.status === 'RequiresCaptcha') {
          clear();
          setCaptchaImg(data.captchaImage);
          setUserInput('');
          setProgressStep(2);
          setDirection('forward');
          setStep('captcha');
        } else if (data.status === 'Failed' || data.status === 'Expired') {
          clear();
          setErrorMsg(data.errorMessage || 'Сессия истекла или произошла ошибка');
          setDirection('forward');
          setStep('error');
        }
      } catch (e: unknown) {
        console.error(e);
      }
    }, 4000);

    return () => {
      clearInterval(pollTimer);
      clearInterval(progTimer);
    };
  }, [step, sessionId]);

  const handleStartAuth = async () => {
    if (!login || !password) return;
    setProgressText('Открываем безопасное соединение...');
    setProgressStep(1);
    setDirection('forward');
    setStep('polling');
    setErrorMsg('');
    try {
      const res = await fetch(`${SERVER_URL}/api/miniapp/auth/start`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'ngrok-skip-browser-warning': 'true',
        },
        body: JSON.stringify({
          TelegramUserId: tgUserId,
          Login: login,
          Password: password,
        }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      setSessionId(data.sessionId);
    } catch (e: unknown) {
      setErrorMsg(
        'Не удалось связаться с сервером. ' +
          (e instanceof Error ? e.message : String(e))
      );
      setDirection('forward');
      setStep('error');
    }
  };

  const handleSubmitInput = async () => {
    if (!userInput) return;
    setProgressText('Проверяем введённый код...');
    setProgressStep(2);
    setDirection('forward');
    setStep('polling');
    try {
      const res = await fetch(`${SERVER_URL}/api/miniapp/auth/submit-input`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'ngrok-skip-browser-warning': 'true',
        },
        body: JSON.stringify({ SessionId: sessionId, Input: userInput }),
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
    } catch (e: unknown) {
      setErrorMsg(
        'Ошибка отправки кода. ' + (e instanceof Error ? e.message : String(e))
      );
      setDirection('forward');
      setStep('error');
    }
  };

  const renderProgressHeader = () => {
    if (step === 'welcome' || step === 'loading') return null;
    
    const getFillWidth = (seg: number) => {
      if (seg <= progressStep) return '100%';
      return '0%';
    };

    return (
      <div className="segmented-progress">
        {[1, 2, 3].map((seg) => (
          <div key={seg} className="progress-segment">
            <motion.div
              className="progress-segment-fill"
              initial={{ width: '0%' }}
              animate={{ width: getFillWidth(seg) }}
              transition={{ duration: 0.35, ease: 'easeInOut' }}
            />
          </div>
        ))}
      </div>
    );
  };

  return (
    <AnimatePresence mode="popLayout" custom={direction}>
      {step === 'loading' && (
        <motion.div
          key="loading"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          className="screen screen--center"
        >
          <div className="spinner" />
        </motion.div>
      )}

      {step === 'welcome' && (
        <motion.div
          key="welcome"
          custom={direction}
          variants={screenVariants}
          initial="initial"
          animate="animate"
          exit="exit"
          className="screen"
        >
          <div className="screen-content">
            <div className="brand">
              <h1 className="brand-title">Mirage Diary</h1>
              <p className="brand-sub">
                {tgFirstName ? `Привет, ${tgFirstName}! ` : ''}
                Подключите электронный дневник, чтобы получать расписание и оценки прямо в Telegram.
              </p>
            </div>
          </div>

          <motion.div
            variants={staggerContainer}
            initial="initial"
            animate="animate"
            className="screen-body glass-card backdrop-filter p-6"
          >
            <motion.div variants={staggerItem} className="feature-item">
              <div className="feature-icon">
                <Fingerprint size={18} strokeWidth={2} />
              </div>
              <div className="feature-text">
                <h3 className="feature-title">Безопасный вход</h3>
                <p className="feature-desc">Авторизация происходит через официальный портал Госуслуг</p>
              </div>
            </motion.div>

            <motion.div variants={staggerItem} className="feature-item">
              <div className="feature-icon">
                <ShieldCheck size={18} strokeWidth={2} />
              </div>
              <div className="feature-text">
                <h3 className="feature-title">Защита данных</h3>
                <p className="feature-desc">Ваши пароли не сохраняются на сервере и передаются в зашифрованном виде</p>
              </div>
            </motion.div>

            <motion.div variants={staggerItem} className="feature-item">
              <div className="feature-icon">
                <Send size={18} strokeWidth={2} />
              </div>
              <div className="feature-text">
                <h3 className="feature-title">Удобство в Telegram</h3>
                <p className="feature-desc">Оценки, домашнее задание и расписание всегда под рукой в один клик</p>
              </div>
            </motion.div>
          </motion.div>

          <div className="screen-footer">
            <motion.button
              whileHover={buttonSpring.hover}
              whileTap={buttonSpring.tap}
              className="btn btn--primary btn-enhanced"
              onClick={() => {
                setDirection('forward');
                setStep('login');
              }}
            >
             Продолжить
              <ArrowRight size={14} strokeWidth={2.5} className="ml-2 h-4 w-4" />
            </motion.button>
          </div>
        </motion.div>
      )}

      {step === 'login' && (
        <motion.div
          key="login"
          custom={direction}
          variants={screenVariants}
          initial="initial"
          animate="animate"
          exit="exit"
          className="screen"
        >
          <div className="screen-content">
            {renderProgressHeader()}
            <div className="brand">
              <h1 className="brand-title">Авторизация</h1>
              <p className="brand-sub">Введите данные от аккаунта Госуслуг для безопасной синхронизации</p>
            </div>
          </div>

          <div className="screen-body glass-card backdrop-filter p-6">
            <div className="form-fields">
              <div className="field-floating">
                <input
                  type="text"
                  className="field-input input-enhanced"
                  value={login}
                  onChange={(e) => setLogin(e.target.value)}
                  placeholder=" "
                  autoComplete="username"
                  autoFocus
                />
                <label className="floating-label">
                  Телефон, Email или СНИЛС
                </label>
                <svg className="input-glow-border">
                  <rect pathLength={1} />
                </svg>
              </div>

              <div className="field-floating">
                <input
                  type="password"
                  className="field-input input-enhanced"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  placeholder=" "
                  autoComplete="current-password"
                />
                <label className="floating-label">
                  Пароль
                </label>
                <svg className="input-glow-border">
                  <rect pathLength={1} />
                </svg>
              </div>
            </div>
          </div>

          <div className="screen-footer">
            <motion.button
              whileHover={buttonSpring.hover}
              whileTap={buttonSpring.tap}
              className="btn btn--primary btn-enhanced"
              onClick={handleStartAuth}
              disabled={!login || !password}
            >
              Войти
              <ArrowRight size={14} strokeWidth={2.5} className="ml-2 h-4 w-4" />
            </motion.button>
            <motion.button
              whileHover={buttonSpring.hover}
              whileTap={buttonSpring.tap}
              className="btn btn--ghost btn-enhanced"
              onClick={() => {
                setDirection('backward');
                setStep('welcome');
              }}
            >
              Назад
            </motion.button>
          </div>
        </motion.div>
      )}

      {step === 'otp' && (
        <motion.div
          key="otp"
          custom={direction}
          variants={screenVariants}
          initial="initial"
          animate="animate"
          exit="exit"
          className="screen"
        >
          <div className="screen-content">
            {renderProgressHeader()}
            <div className="brand">
              <h1 className="brand-title">Двухфакторная защита</h1>
              <p className="brand-sub">Введите код подтверждения из СМС или приложения «Госключ»</p>
            </div>
          </div>

          <div className="screen-body glass-card backdrop-filter p-6">
            <div className="field" style={{ position: 'relative' }}>
              <input
                type="text"
                className="field-input field-input--code"
                value={userInput}
                onChange={(e) => setUserInput(e.target.value)}
                placeholder="••••••"
                maxLength={6}
                autoFocus
                inputMode="numeric"
              />
              <svg className="input-glow-border">
                <rect pathLength={1} />
              </svg>
            </div>
          </div>

          <div className="screen-footer">
            <motion.button
              whileHover={buttonSpring.hover}
              whileTap={buttonSpring.tap}
              className="btn btn--primary btn-enhanced"
              onClick={handleSubmitInput}
              disabled={userInput.length < 4}
            >
              Подтвердить
            </motion.button>
          </div>
        </motion.div>
      )}

      {step === 'captcha' && (
        <motion.div
          key="captcha"
          custom={direction}
          variants={screenVariants}
          initial="initial"
          animate="animate"
          exit="exit"
          className="screen"
        >
          <div className="screen-content">
            {renderProgressHeader()}
            <div className="brand">
              <h1 className="brand-title">Проверка безопасности</h1>
              <p className="brand-sub">Введите символы, изображённые на картинке ниже</p>
            </div>
          </div>

          <div className="screen-body glass-card backdrop-filter p-6">
            <div className="captcha-wrap">
              {captchaImg && (
                <img
                  src={`data:image/jpeg;base64,${captchaImg}`}
                  alt="Captcha"
                  className="captcha-img"
                />
              )}
            </div>

            <div className="field" style={{ position: 'relative' }}>
              <input
                type="text"
                className="field-input field-input--code"
                value={userInput}
                onChange={(e) => setUserInput(e.target.value)}
                placeholder="Введите код"
                autoFocus
              />
              <svg className="input-glow-border">
                <rect pathLength={1} />
              </svg>
            </div>
          </div>

          <div className="screen-footer">
            <motion.button
              whileHover={buttonSpring.hover}
              whileTap={buttonSpring.tap}
              className="btn btn--primary btn-enhanced"
              onClick={handleSubmitInput}
              disabled={!userInput}
            >
              Продолжить
            </motion.button>
          </div>
        </motion.div>
      )}

      {step === 'polling' && (
        <motion.div
          key="polling"
          custom={direction}
          initial="initial"
          animate="animate"
          exit="exit"
          variants={screenVariants}
          className="screen screen--center"
        >
          <div className="status-wrapper">
            {renderProgressHeader()}
            <div className="spinner" />
            <div className="status-text-group">
              <p className="progress-title">{progressText}</p>
              <p className="progress-hint">{progressText.includes('Готово') ? '' : 'Это может занять до 30 секунд'}</p>
            </div>
          </div>
        </motion.div>
      )}

      {step === 'success' && (
        <motion.div
          key="success"
          custom={direction}
          initial="initial"
          animate="animate"
          exit={{ opacity: 0 }}
          variants={screenVariants}
          className="screen screen--center"
        >
          <div className="status-wrapper">
            {renderProgressHeader()}
            <div className="status-icon status-icon--success">
              <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" style={{ display: 'block' }}>
                <motion.path
                  d="M20 6L9 17L4 12"
                  initial={{ pathLength: 0 }}
                  animate={{ pathLength: 1 }}
                  transition={{ duration: 0.45, ease: "easeOut", delay: 0.15 }}
                />
              </svg>
            </div>
            <div className="status-text-group">
              <p className="status-title">Дневник подключён</p>
              <p className="status-sub">Авторизация успешно завершена. Окно закроется автоматически через несколько секунд.</p>
            </div>
          </div>
        </motion.div>
      )}

      {step === 'error' && (
        <motion.div
          key="error"
          custom={direction}
          initial="initial"
          animate="animate"
          exit="exit"
          variants={screenVariants}
          className="screen screen--center"
        >
          <div className="status-wrapper">
            <div className="status-icon status-icon--error">
              <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" style={{ display: 'block' }}>
                <motion.line
                  x1="18" y1="6" x2="6" y2="18"
                  initial={{ pathLength: 0 }}
                  animate={{ pathLength: 1 }}
                  transition={{ duration: 0.35, ease: "easeOut", delay: 0.1 }}
                />
                <motion.line
                  x1="6" y1="6" x2="18" y2="18"
                  initial={{ pathLength: 0 }}
                  animate={{ pathLength: 1 }}
                  transition={{ duration: 0.35, ease: "easeOut", delay: 0.2 }}
                />
              </svg>
            </div>
            <div className="status-text-group">
              <p className="status-title">Ошибка входа</p>
              <p className="status-sub">{errorMsg}</p>
            </div>
            <div className="status-actions">
              <motion.button
                whileHover={buttonSpring.hover}
                whileTap={buttonSpring.tap}
                className="btn btn--primary btn-enhanced"
                onClick={() => {
                  setDirection('backward');
                  setStep('login');
                }}
              >
                Попробовать снова
              </motion.button>
            </div>
          </div>
        </motion.div>
      )}

      {step === 'already_authorized' && (
        <motion.div
          key="already_authorized"
          custom={direction}
          initial="initial"
          animate="animate"
          exit="exit"
          variants={screenVariants}
          className="screen screen--center"
        >
          <div className="status-wrapper">
            <div className="status-icon status-icon--success">
              <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" style={{ display: 'block' }}>
                <motion.path
                  d="M20 6L9 17L4 12"
                  initial={{ pathLength: 0 }}
                  animate={{ pathLength: 1 }}
                  transition={{ duration: 0.45, ease: "easeOut", delay: 0.15 }}
                />
              </svg>
            </div>
            <div className="status-text-group">
              <p className="status-title">Вы уже авторизованы</p>
              <p className="status-sub">Электронный дневник успешно подключён. Вы можете вернуться в Telegram.</p>
            </div>
            <div className="status-actions">
              <motion.button
                whileHover={buttonSpring.hover}
                whileTap={buttonSpring.tap}
                className="btn btn--primary btn-enhanced"
                onClick={() => window.Telegram?.WebApp?.close?.()}
              >
                Вернуться в Telegram
              </motion.button>
              <motion.button
                whileHover={buttonSpring.hover}
                whileTap={buttonSpring.tap}
                className="btn btn--ghost btn-enhanced"
                onClick={() => {
                  setDirection('backward');
                  setStep('welcome');
                }}
              >
                Войти под другим аккаунтом
              </motion.button>
            </div>
          </div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
