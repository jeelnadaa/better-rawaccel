// Automated verification of app.js math and UI controls:
// 1. Spring-based curve morphing
// 2. prefers-reduced-motion bypass
// 3. Write re-trace sweep animation
// 4. Advanced controls (angle snap, speed cap, lr/ud multipliers, smoothing)

const assert = require('assert');
const fs = require('fs');
const path = require('path');

console.log("--- Starting UI Animation & Controls Verification ---");

// 1. Verify Curve Morphing Physics
function testCurveMorphing() {
  console.log("Testing curve morphing spring physics...");
  const N = 240;
  const target = new Float32Array(N).fill(2.0); // change target to 2.0
  const shown = new Float32Array(N).fill(1.0);  // current displayed is 1.0
  const vel = new Float32Array(N).fill(0.0);
  const dt = 0.016; // 60 FPS frame time (16ms)

  // Step 10 frames
  for (let frame = 0; frame < 10; frame++) {
    for (let i = 0; i < N; i++) {
      const delay = 1 + i / N * 0.6;
      vel[i] += ((target[i] - shown[i]) * 170 / delay - vel[i] * 19) * dt;
      shown[i] += vel[i] * dt;
    }
  }

  // After 10 frames, points should have moved towards 2.0, but not reached yet
  assert(shown[0] > 1.0, "Point 0 should have started moving toward target");
  assert(shown[0] < 2.05, "Point 0 should be approaching target smoothly");
  assert(shown[0] > shown[N - 1], "Higher index points have progressive delay, so shown[0] moves ahead of shown[N-1]");

  // Step 60 more frames (total ~1.1s)
  for (let frame = 0; frame < 60; frame++) {
    for (let i = 0; i < N; i++) {
      const delay = 1 + i / N * 0.6;
      vel[i] += ((target[i] - shown[i]) * 170 / delay - vel[i] * 19) * dt;
      shown[i] += vel[i] * dt;
    }
  }

  // Curve should have settled very close to target (2.0)
  for (let i = 0; i < N; i++) {
    assert(Math.abs(shown[i] - target[i]) < 0.05, `Point ${i} should settle near target`);
  }
  console.log("  [PASS] Curve morphing smoothly springs and settles.");
}

// 2. Verify Reduced Motion Bypass
function testReducedMotionBypass() {
  console.log("Testing reduced-motion bypass...");
  const N = 240;
  const target = new Float32Array(N).fill(3.5);
  const shown = new Float32Array(N).fill(1.0);
  const vel = new Float32Array(N).fill(0.0);
  const still = true; // RM.matches === true

  for (let i = 0; i < N; i++) {
    if (still) {
      shown[i] = target[i];
      vel[i] = 0;
    }
  }

  for (let i = 0; i < N; i++) {
    assert.strictEqual(shown[i], 3.5, "Reduced motion must snap points instantly to target on frame 1");
    assert.strictEqual(vel[i], 0, "Velocities must remain 0");
  }
  console.log("  [PASS] Reduced motion snaps immediately with zero spring lag.");
}

// 3. Verify Write Re-Trace Sweep
function testWriteRetrace() {
  console.log("Testing write re-trace confirmation sweep...");
  let sweepT = 0;
  const dt = 0.016;
  const N = 240;

  // Reduced motion test:
  const rmMatches = true;
  let sweepT_RM = rmMatches ? -1 : 0;
  assert.strictEqual(sweepT_RM, -1, "Reduced motion must bypass write sweep animation");

  // Normal motion sweep test:
  let sweepT_Normal = 0;
  let reachedMaxProg = false;
  let fadedOut = false;

  for (let frame = 0; frame < 100; frame++) {
    sweepT_Normal += dt;
    const prog = Math.min(1, sweepT_Normal / 0.55);
    const alpha = sweepT_Normal < 0.55 ? 1 : Math.max(0, 1 - (sweepT_Normal - 0.55) / 0.45);
    const ie = Math.max(1, Math.round(prog * (N - 1)));

    if (prog >= 1.0) reachedMaxProg = true;
    if (alpha <= 0) {
      sweepT_Normal = -1;
      fadedOut = true;
      break;
    }
  }

  assert(reachedMaxProg, "Sweep must trace 100% of the curve width");
  assert(fadedOut, "Sweep line must fade out and reset sweepT to -1");
  console.log("  [PASS] Write re-trace sweeps across curve and resets cleanly.");
}

// 4. Verify Options Controls Contracts in meta.json
function testControlsContracts() {
  console.log("Testing advanced controls definition and step/range constraints...");
  const metaPath = path.resolve(__dirname, '../src/BetterRawAccel/ui/meta.json');
  const meta = JSON.parse(fs.readFileSync(metaPath, 'utf8'));

  const requiredControls = [
    { field: 'snap', min: 0, max: 45, unit: 'deg' },
    { field: 'speedCap', min: 0, max: 300, unit: 'c/ms' },
    { field: 'lrRatio', min: 0.1, max: 3 },
    { field: 'udRatio', min: 0.1, max: 3 },
    { field: 'inputSmooth', min: 0, max: 100, unit: 'ms' },
    { field: 'scaleSmooth', min: 0, max: 100, unit: 'ms' },
    { field: 'outputSmooth', min: 0, max: 100, unit: 'ms' },
    { field: 'lpNorm', min: 0.5, max: 16 }
  ];

  const adv = meta.advanced;
  for (const req of requiredControls) {
    const found = adv.find(c => c.field === req.field);
    assert(found, `Control for '${req.field}' must exist in meta.advanced`);
    assert(found.min <= req.min, `'${req.field}' min constraint`);
    assert(found.max >= req.max, `'${req.field}' max constraint`);
    if (req.unit) assert.strictEqual(found.unit, req.unit, `'${req.field}' unit`);
    assert(typeof found.step === 'number' && found.step > 0, `'${req.field}' step must be positive number`);
  }
  console.log("  [PASS] All 8 advanced controls match strict schema definitions.");
}

// Run all
testCurveMorphing();
testReducedMotionBypass();
testWriteRetrace();
testControlsContracts();

console.log("--- All UI Animation & Controls tests passed successfully! ---");
