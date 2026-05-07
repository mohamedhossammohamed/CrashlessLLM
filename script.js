const copyButton = document.querySelector("#copyCommand");
const commandPill = document.querySelector("#commandPill");

async function copyText(text) {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }

  const input = document.createElement("textarea");
  input.value = text;
  input.setAttribute("readonly", "");
  input.style.position = "fixed";
  input.style.opacity = "0";
  document.body.appendChild(input);
  input.select();
  document.execCommand("copy");
  document.body.removeChild(input);
}

if (copyButton) {
  const originalText = copyButton.textContent;
  copyButton.addEventListener("click", async () => {
    const command = copyButton.dataset.command ?? "dotnet add package CrashlessLLM --version 0.2.0-alpha";
    try {
      await copyText(command);
      copyButton.textContent = "Copied";
      copyButton.classList.add("copied");
      if (commandPill) {
        commandPill.textContent = command;
      }
      window.setTimeout(() => {
        copyButton.textContent = originalText;
        copyButton.classList.remove("copied");
      }, 1600);
    } catch {
      copyButton.textContent = "Select command below";
      if (commandPill) {
        commandPill.textContent = command;
      }
    }
  });
}

const canvas = document.querySelector("#memoryCanvas");
const context = canvas?.getContext("2d");
const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

if (canvas && context) {
  let width = 0;
  let height = 0;
  let dpr = 1;
  let particles = [];
  let tokens = [];

  const random = (min, max) => min + Math.random() * (max - min);

  function resizeCanvas() {
    const rect = canvas.getBoundingClientRect();
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    width = rect.width;
    height = rect.height;
    canvas.width = Math.floor(width * dpr);
    canvas.height = Math.floor(height * dpr);
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
    particles = Array.from({ length: reduceMotion ? 18 : 54 }, createParticle);
    tokens = Array.from({ length: reduceMotion ? 4 : 12 }, createToken);
  }

  function createParticle() {
    return {
      x: random(8, width * 0.28),
      y: random(height * 0.16, height * 0.84),
      vx: random(0.55, 1.85),
      vy: random(-0.85, 0.85),
      size: random(1.4, 3.8),
      heat: random(0.35, 1),
      phase: random(0, Math.PI * 2),
      spin: random(-0.045, 0.045)
    };
  }

  function createToken() {
    const shieldX = width * 0.52;
    const shieldRadius = Math.min(width, height) * 0.16;
    return {
      x: random(shieldX + shieldRadius * 0.8, width * 0.84),
      y: random(height * 0.44, height * 0.57),
      speed: random(0.7, 1.2),
      width: random(26, 58),
      alpha: random(0.35, 1)
    };
  }

  function drawParticle(particle, shieldX, shieldY, shieldRadius) {
    const dx = shieldX - particle.x;
    const dy = shieldY - particle.y;
    const distance = Math.hypot(dx, dy);
    const nearShield = distance < shieldRadius * 1.55;

    if (!reduceMotion) {
      particle.phase += 0.055;
      particle.x += particle.vx + Math.sin(particle.phase) * 0.9;
      particle.y += particle.vy + Math.cos(particle.phase * 1.7) * 0.7;
    }

    if (nearShield) {
      const angle = Math.atan2(dy, dx) + Math.PI;
      particle.x = shieldX + Math.cos(angle) * shieldRadius * random(1.02, 1.09);
      particle.y = shieldY + Math.sin(angle) * shieldRadius * random(1.02, 1.09);
      particle.vx *= -0.45;
      particle.vy += Math.sin(particle.phase) * 1.4;
      particle.heat *= 0.985;
    }

    if (particle.x > shieldX + shieldRadius * 0.18 || particle.y < -30 || particle.y > height + 30 || particle.heat < 0.08) {
      Object.assign(particle, createParticle());
      particle.x = random(8, width * 0.2);
    }

    const alpha = nearShield ? 0.86 : 0.36 + particle.heat * 0.42;
    const gradient = context.createRadialGradient(particle.x, particle.y, 0, particle.x, particle.y, particle.size * 6);
    gradient.addColorStop(0, `rgba(255, 77, 109, ${alpha})`);
    gradient.addColorStop(0.45, `rgba(255, 120, 82, ${alpha * 0.32})`);
    gradient.addColorStop(1, "rgba(255, 77, 109, 0)");
    context.fillStyle = gradient;
    context.beginPath();
    context.arc(particle.x, particle.y, particle.size * 6, 0, Math.PI * 2);
    context.fill();

    context.fillStyle = `rgba(255, 206, 214, ${Math.min(1, alpha + 0.1)})`;
    context.beginPath();
    context.arc(particle.x, particle.y, particle.size, 0, Math.PI * 2);
    context.fill();
  }

  function drawToken(token, shieldX, shieldY, shieldRadius) {
    if (!reduceMotion) {
      token.x += token.speed;
      token.alpha += 0.018;
    }

    const targetX = width * 0.89;
    if (token.x > targetX) {
      Object.assign(token, createToken());
      token.x = shieldX + shieldRadius * 0.75;
      token.alpha = 0.1;
    }

    const y = token.y + Math.sin((token.x + performance.now() * 0.05) * 0.018) * 3;
    const alpha = Math.min(0.86, token.alpha);
    context.save();
    context.globalAlpha = alpha;
    context.fillStyle = "rgba(103, 232, 249, 0.16)";
    context.strokeStyle = "rgba(139, 255, 191, 0.34)";
    context.lineWidth = 1;
    roundRect(context, token.x, y, token.width, 12, 8);
    context.fill();
    context.stroke();
    context.fillStyle = "rgba(225, 255, 250, 0.78)";
    context.beginPath();
    context.arc(token.x + 8, y + 6, 2, 0, Math.PI * 2);
    context.fill();
    context.restore();
  }

  function roundRect(ctx, x, y, w, h, r) {
    const radius = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    ctx.moveTo(x + radius, y);
    ctx.arcTo(x + w, y, x + w, y + h, radius);
    ctx.arcTo(x + w, y + h, x, y + h, radius);
    ctx.arcTo(x, y + h, x, y, radius);
    ctx.arcTo(x, y, x + w, y, radius);
    ctx.closePath();
  }

  function drawConnections(shieldX, shieldY, shieldRadius) {
    const leftGradient = context.createLinearGradient(0, shieldY, shieldX, shieldY);
    leftGradient.addColorStop(0, "rgba(255, 77, 109, 0.0)");
    leftGradient.addColorStop(1, "rgba(255, 77, 109, 0.32)");
    context.strokeStyle = leftGradient;
    context.lineWidth = 1;
    for (let i = 0; i < 5; i += 1) {
      const y = shieldY + (i - 2) * 34;
      context.beginPath();
      context.moveTo(width * 0.08, y + Math.sin(performance.now() * 0.002 + i) * 10);
      context.bezierCurveTo(width * 0.24, y - 28, width * 0.34, y + 28, shieldX - shieldRadius * 0.88, y * 0.84 + shieldY * 0.16);
      context.stroke();
    }

    const rightGradient = context.createLinearGradient(shieldX, shieldY, width, shieldY);
    rightGradient.addColorStop(0, "rgba(103, 232, 249, 0.4)");
    rightGradient.addColorStop(1, "rgba(139, 255, 191, 0.08)");
    context.strokeStyle = rightGradient;
    context.lineWidth = 1.4;
    for (let i = 0; i < 3; i += 1) {
      const y = shieldY + (i - 1) * 26;
      context.beginPath();
      context.moveTo(shieldX + shieldRadius * 0.76, y);
      context.bezierCurveTo(width * 0.66, y - 12, width * 0.74, y + 12, width * 0.83, y);
      context.stroke();
    }
  }

  function draw() {
    context.clearRect(0, 0, width, height);
    const shieldX = width * 0.52;
    const shieldY = height * 0.5;
    const shieldRadius = Math.min(width, height) * 0.17;

    drawConnections(shieldX, shieldY, shieldRadius);

    for (const particle of particles) {
      drawParticle(particle, shieldX, shieldY, shieldRadius);
    }

    const shieldGlow = context.createRadialGradient(shieldX, shieldY, shieldRadius * 0.58, shieldX, shieldY, shieldRadius * 1.75);
    shieldGlow.addColorStop(0, "rgba(103, 232, 249, 0.0)");
    shieldGlow.addColorStop(0.52, "rgba(103, 232, 249, 0.08)");
    shieldGlow.addColorStop(1, "rgba(103, 232, 249, 0)");
    context.fillStyle = shieldGlow;
    context.beginPath();
    context.arc(shieldX, shieldY, shieldRadius * 1.75, 0, Math.PI * 2);
    context.fill();

    for (const token of tokens) {
      drawToken(token, shieldX, shieldY, shieldRadius);
    }

    if (!reduceMotion) {
      requestAnimationFrame(draw);
    }
  }

  resizeCanvas();
  draw();
  window.addEventListener("resize", resizeCanvas);
}
