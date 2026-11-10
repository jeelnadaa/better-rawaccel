/**
 * accel. — Interactive Engine, Aim Simulator, Curve Visualizer, Scroll Animations & Theme System
 */

(function () {
  'use strict';

  // ---------------- 1. Theme Switcher ----------------
  const themeSelects = document.querySelectorAll('.theme-select');
  const storedTheme = localStorage.getItem('accel.theme') || 'paper';
  setTheme(storedTheme);

  themeSelects.forEach(sel => {
    sel.value = storedTheme;
    sel.addEventListener('change', e => setTheme(e.target.value));
  });

  function setTheme(t) {
    document.documentElement.dataset.theme = t;
    localStorage.setItem('accel.theme', t);
    themeSelects.forEach(sel => { if (sel.value !== t) sel.value = t; });
    window.dispatchEvent(new CustomEvent('themeChanged', { detail: t }));
  }

  // ---------------- 2. Scroll Reveal Animations ----------------
  const revealElements = document.querySelectorAll('.reveal');
  if ('IntersectionObserver' in window && revealElements.length > 0) {
    const observer = new IntersectionObserver((entries, obs) => {
      entries.forEach(entry => {
        if (entry.isIntersecting) {
          entry.target.classList.add('is-visible');
          obs.unobserve(entry.target);
        }
      });
    }, {
      threshold: 0.12,
      rootMargin: '0px 0px -40px 0px'
    });

    revealElements.forEach(el => observer.observe(el));
  } else {
    revealElements.forEach(el => el.classList.add('is-visible'));
  }

  // ---------------- 3. Hero Mini Curve Visualizer ----------------
  const heroCv = document.getElementById('heroCurveCanvas');
  if (heroCv) {
    const ctx = heroCv.getContext('2d');
    let W, H;
    const N = 120;
    const curvePoints = new Float32Array(N);
    let heroMarkerSpeed = 0;
    let heroMarkerTarget = 0;

    function resizeHeroCv() {
      const rect = heroCv.getBoundingClientRect();
      const dpr = window.devicePixelRatio || 1;
      W = rect.width;
      H = rect.height;
      heroCv.width = W * dpr;
      heroCv.height = H * dpr;
      ctx.resetTransform?.();
      ctx.scale(dpr, dpr);
    }
    window.addEventListener('resize', resizeHeroCv);
    resizeHeroCv();

    function computeHeroCurve() {
      const offset = 2.0;
      const limit = 2.0;
      const decay = 0.08;
      for (let i = 0; i < N; i++) {
        const v = (i / (N - 1)) * 35;
        if (v <= offset) {
          curvePoints[i] = 1.0;
        } else {
          curvePoints[i] = 1.0 + (limit - 1.0) * (1.0 - Math.exp(-decay * (v - offset)));
        }
      }
    }
    computeHeroCurve();

    let lastHeroTime = performance.now();
    function renderHero(now) {
      const dt = Math.min((now - lastHeroTime) / 1000, 0.1);
      lastHeroTime = now;

      const t = now * 0.0015;
      heroMarkerTarget = 14 + Math.sin(t * 1.5) * 11 + Math.sin(t * 3.7) * 4;
      heroMarkerSpeed += (heroMarkerTarget - heroMarkerSpeed) * Math.min(1, dt * 6);

      ctx.clearRect(0, 0, W, H);

      const isDark = document.documentElement.dataset.theme !== 'paper';
      ctx.strokeStyle = isDark ? 'rgba(255,255,255,0.06)' : 'rgba(0,0,0,0.06)';
      ctx.lineWidth = 1;
      const padding = { l: 36, r: 16, t: 16, b: 24 };
      const pw = W - padding.l - padding.r;
      const ph = H - padding.t - padding.b;

      for (let yStep = 1.0; yStep <= 2.0; yStep += 0.5) {
        const yPos = padding.t + ph * (1 - (yStep - 0.8) / 1.4);
        ctx.beginPath();
        ctx.moveTo(padding.l, yPos);
        ctx.lineTo(padding.l + pw, yPos);
        ctx.stroke();

        ctx.font = '10px "IBM Plex Mono", monospace';
        ctx.fillStyle = isDark ? 'rgba(255,255,255,0.45)' : 'rgba(0,0,0,0.45)';
        ctx.fillText(yStep.toFixed(1) + '×', 6, yPos + 3);
      }

      ctx.beginPath();
      for (let i = 0; i < N; i++) {
        const x = padding.l + (i / (N - 1)) * pw;
        const y = padding.t + ph * (1 - (curvePoints[i] - 0.8) / 1.4);
        if (i === 0) ctx.moveTo(x, y);
        else ctx.lineTo(x, y);
      }
      ctx.strokeStyle = '#e5484d';
      ctx.lineWidth = 2.5;
      ctx.lineCap = 'round';
      ctx.stroke();

      const markerFrac = Math.max(0, Math.min(1, heroMarkerSpeed / 35));
      const mx = padding.l + markerFrac * pw;
      const curveIdx = Math.round(markerFrac * (N - 1));
      const currentSens = curvePoints[curveIdx] || 1.0;
      const my = padding.t + ph * (1 - (currentSens - 0.8) / 1.4);

      ctx.strokeStyle = isDark ? '#fff' : '#000';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(mx, padding.t); ctx.lineTo(mx, padding.t + ph);
      ctx.moveTo(padding.l, my); ctx.lineTo(padding.l + pw, my);
      ctx.stroke();

      ctx.fillStyle = '#e5484d';
      ctx.beginPath();
      ctx.arc(mx, my, 4.5, 0, Math.PI * 2);
      ctx.fill();

      const sIn = document.getElementById('heroSensIn');
      const sOut = document.getElementById('heroSensOut');
      const sMul = document.getElementById('heroSensMul');
      const sCm = document.getElementById('heroSensCm');
      if (sIn) sIn.textContent = heroMarkerSpeed.toFixed(1);
      if (sOut) sOut.textContent = (heroMarkerSpeed * currentSens).toFixed(1);
      if (sMul) sMul.textContent = currentSens.toFixed(2) + '×';
      if (sCm) {
        const effSens = 0.35 * currentSens;
        const cm = (360 / (effSens * 0.07) / 800) * 2.54;
        sCm.textContent = Math.round(cm) + ' cm';
      }

      requestAnimationFrame(renderHero);
    }
    requestAnimationFrame(renderHero);
  }

  // ---------------- 4. In-Browser Aim Simulator & Interactive Demo ----------------
  const aimCv = document.getElementById('aimCanvas');
  if (aimCv) {
    const actx = aimCv.getContext('2d');
    let aW, aH;
    let aimX = 0, aimY = 0;
    let targetsHit = 0;
    let totalClicks = 0;

    let accelEnabled = true;
    let userDpi = 800;
    let baseSens = 0.35;
    let curveType = 'natural';
    let curveLimit = 1.8;
    let curveOffset = 3.0;

    let targets = [];
    let hitRipples = [];

    function spawnTarget(type = 'normal') {
      const radius = type === 'micro' ? 10 : 22;
      return {
        x: radius + Math.random() * (aW - radius * 2),
        y: radius + Math.random() * (aH - radius * 2),
        radius,
        type,
        color: type === 'micro' ? '#e5484d' : '#1b63b5',
        spawnedAt: performance.now(),
        pulse: 0
      };
    }

    function initTargets() {
      targets = [];
      targets.push(spawnTarget('micro'));
      targets.push(spawnTarget('normal'));
      targets.push(spawnTarget('normal'));
    }

    function resizeAimCv() {
      const rect = aimCv.getBoundingClientRect();
      const dpr = window.devicePixelRatio || 1;
      aW = rect.width;
      aH = rect.height;
      aimCv.width = aW * dpr;
      aimCv.height = aH * dpr;
      actx.resetTransform?.();
      actx.scale(dpr, dpr);
      if (aimX === 0) { aimX = aW / 2; aimY = aH / 2; }
      initTargets();
    }
    window.addEventListener('resize', resizeAimCv);
    resizeAimCv();

    const dpiSelect = document.getElementById('demoDpi');
    const modeSelect = document.getElementById('demoCurveType');
    const limitSlider = document.getElementById('demoLimit');
    const limitVal = document.getElementById('demoLimitVal');
    const btnAccelOn = document.getElementById('btnAccelOn');
    const btnAccelOff = document.getElementById('btnAccelOff');
    const hitsEl = document.getElementById('demoHits');
    const accEl = document.getElementById('demoAccuracy');
    const liveSpeedEl = document.getElementById('demoLiveSpeed');
    const liveSensEl = document.getElementById('demoLiveSens');
    const liveCmEl = document.getElementById('demoLiveCm');

    if (dpiSelect) dpiSelect.addEventListener('change', e => { userDpi = parseInt(e.target.value, 10); });
    if (modeSelect) modeSelect.addEventListener('change', e => { curveType = e.target.value; });
    if (limitSlider) {
      limitSlider.addEventListener('input', e => {
        curveLimit = parseFloat(e.target.value);
        if (limitVal) limitVal.textContent = curveLimit.toFixed(1) + '×';
      });
    }

    if (btnAccelOn && btnAccelOff) {
      btnAccelOn.addEventListener('click', () => {
        accelEnabled = true;
        btnAccelOn.classList.add('active');
        btnAccelOff.classList.remove('active');
      });
      btnAccelOff.addEventListener('click', () => {
        accelEnabled = false;
        btnAccelOff.classList.add('active');
        btnAccelOn.classList.remove('active');
      });
    }

    let lastMoveTime = performance.now();
    let currentHandSpeed = 0;
    let smoothedMultiplier = 1.0;

    aimCv.addEventListener('mousemove', e => {
      const now = performance.now();
      const dt = Math.max(1, now - lastMoveTime);
      lastMoveTime = now;

      const rect = aimCv.getBoundingClientRect();
      const rawX = e.clientX - rect.left;
      const rawY = e.clientY - rect.top;

      const dx = rawX - aimX;
      const dy = rawY - aimY;
      const distPx = Math.sqrt(dx * dx + dy * dy);

      const counts = distPx * (userDpi / 96);
      const measuredSpeed = counts / dt;

      currentHandSpeed = measuredSpeed;

      let mult = 1.0;
      if (accelEnabled) {
        if (curveType === 'natural') {
          if (currentHandSpeed > curveOffset) {
            mult = 1.0 + (curveLimit - 1.0) * (1.0 - Math.exp(-0.09 * (currentHandSpeed - curveOffset)));
          }
        } else if (curveType === 'linear') {
          if (currentHandSpeed > curveOffset) {
            mult = Math.min(curveLimit, 1.0 + 0.05 * (currentHandSpeed - curveOffset));
          }
        } else if (curveType === 'jump') {
          mult = currentHandSpeed > 10.0 ? curveLimit : 1.0;
        }
      }

      smoothedMultiplier += (mult - smoothedMultiplier) * 0.4;

      aimX += dx * (accelEnabled ? mult : 1.0);
      aimY += dy * (accelEnabled ? mult : 1.0);

      aimX = Math.max(8, Math.min(aW - 8, aimX));
      aimY = Math.max(8, Math.min(aH - 8, aimY));
    });

    aimCv.addEventListener('mousedown', () => {
      totalClicks++;
      let hit = false;
      for (let i = targets.length - 1; i >= 0; i--) {
        const t = targets[i];
        const dist = Math.hypot(aimX - t.x, aimY - t.y);
        if (dist <= t.radius + 3) {
          targetsHit++;
          hit = true;
          // Spawn expanding hit ripple
          hitRipples.push({ x: t.x, y: t.y, r: t.radius, color: t.color, alpha: 1.0 });
          targets.splice(i, 1);
          targets.push(spawnTarget(t.type));
          break;
        }
      }

      if (hitsEl) hitsEl.textContent = targetsHit;
      if (accEl) accEl.textContent = Math.round((targetsHit / Math.max(1, totalClicks)) * 100) + '%';
    });

    function drawAimDemo(now) {
      actx.clearRect(0, 0, aW, aH);

      if (now - lastMoveTime > 60) {
        currentHandSpeed *= 0.85;
        if (currentHandSpeed < 0.05) currentHandSpeed = 0;
      }

      const isDark = document.documentElement.dataset.theme !== 'paper';

      actx.strokeStyle = isDark ? 'rgba(255,255,255,0.04)' : 'rgba(0,0,0,0.04)';
      actx.lineWidth = 1;
      actx.beginPath();
      actx.moveTo(aW / 2, 0); actx.lineTo(aW / 2, aH);
      actx.moveTo(0, aH / 2); actx.lineTo(aW, aH / 2);
      actx.stroke();

      // Render Expanding Hit Ripples
      for (let i = hitRipples.length - 1; i >= 0; i--) {
        const rp = hitRipples[i];
        rp.r += 1.8;
        rp.alpha -= 0.04;
        if (rp.alpha <= 0) {
          hitRipples.splice(i, 1);
          continue;
        }
        actx.save();
        actx.globalAlpha = Math.max(0, rp.alpha);
        actx.strokeStyle = rp.color;
        actx.lineWidth = 2;
        actx.beginPath();
        actx.arc(rp.x, rp.y, rp.r, 0, Math.PI * 2);
        actx.stroke();
        actx.restore();
      }

      // Render Targets
      targets.forEach(t => {
        t.pulse += 0.04;
        const scale = 1 + Math.sin(t.pulse) * 0.06;

        actx.save();
        actx.translate(t.x, t.y);
        actx.scale(scale, scale);

        actx.beginPath();
        actx.arc(0, 0, t.radius, 0, Math.PI * 2);
        actx.fillStyle = t.type === 'micro' ? 'rgba(229, 72, 77, 0.15)' : 'rgba(27, 99, 181, 0.15)';
        actx.fill();
        actx.strokeStyle = t.color;
        actx.lineWidth = t.type === 'micro' ? 2 : 1.5;
        actx.stroke();

        actx.beginPath();
        actx.arc(0, 0, t.type === 'micro' ? 3 : 5, 0, Math.PI * 2);
        actx.fillStyle = t.color;
        actx.fill();

        actx.restore();
      });

      // Crosshair
      actx.strokeStyle = '#e5484d';
      actx.lineWidth = 1.5;
      const chSize = 7;
      actx.beginPath();
      actx.moveTo(aimX - chSize, aimY); actx.lineTo(aimX + chSize, aimY);
      actx.moveTo(aimX, aimY - chSize); actx.lineTo(aimX, aimY + chSize);
      actx.stroke();

      actx.beginPath();
      actx.arc(aimX, aimY, 2, 0, Math.PI * 2);
      actx.fillStyle = '#e5484d';
      actx.fill();

      if (liveSpeedEl) liveSpeedEl.textContent = currentHandSpeed.toFixed(1) + ' c/ms';
      if (liveSensEl) liveSensEl.textContent = (accelEnabled ? smoothedMultiplier.toFixed(2) : '1.00') + '×';
      if (liveCmEl) {
        const effSens = baseSens * (accelEnabled ? smoothedMultiplier : 1.0);
        const cm = (360 / (effSens * 0.07) / userDpi) * 2.54;
        liveCmEl.textContent = Math.round(cm) + ' cm/360';
      }

      requestAnimationFrame(drawAimDemo);
    }
    requestAnimationFrame(drawAimDemo);
  }

  // ---------------- 5. FAQ Accordion ----------------
  const faqItems = document.querySelectorAll('.faq-item');
  faqItems.forEach(item => {
    const q = item.querySelector('.faq-question');
    const a = item.querySelector('.faq-answer');
    if (!q || !a) return;

    a.style.display = 'none';
    q.addEventListener('click', () => {
      const isOpen = a.style.display === 'block';
      faqItems.forEach(other => {
        const otherA = other.querySelector('.faq-answer');
        if (otherA) otherA.style.display = 'none';
        const otherIcon = other.querySelector('.faq-icon');
        if (otherIcon) otherIcon.textContent = '+';
      });

      a.style.display = isOpen ? 'none' : 'block';
      const icon = item.querySelector('.faq-icon');
      if (icon) icon.textContent = isOpen ? '+' : '−';
    });
  });

  if (faqItems[0]) {
    const firstA = faqItems[0].querySelector('.faq-answer');
    if (firstA) firstA.style.display = 'block';
    const firstIcon = faqItems[0].querySelector('.faq-icon');
    if (firstIcon) firstIcon.textContent = '−';
  }

})();
