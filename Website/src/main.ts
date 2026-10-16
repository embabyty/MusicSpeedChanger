import { API_LATEST, FEATURES, RELEASES_URL } from "./msc-data";
import "./style.css";

// ---------- helpers (pure, exported for testability) ----------

export function parseLength(input: string): number | null {
  const t = input.trim();
  if (/^\d+(\.\d+)?$/.test(t)) return parseFloat(t); // seconds
  const m = t.match(/^(?:(\d+):)?([0-5]?\d):([0-5]\d(?:\.\d+)?)$/);
  if (!m) return null;
  const h = m[1] ? parseInt(m[1], 10) : 0;
  return h * 3600 + parseInt(m[2], 10) * 60 + parseFloat(m[3]);
}

export function formatTime(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  return `${h > 0 ? h + ":" : ""}${mm}:${String(sec).padStart(2, "0")}`;
}

/** Effective duration at a tempo percent (e.g. 150% shortens). */
export function effectiveDuration(originalSeconds: number, tempoPercent: number): number {
  return originalSeconds / (tempoPercent / 100);
}

/** Frequency ratio for a semitone shift: 2^(n/12). */
export function semitoneRatio(semitones: number): number {
  return Math.pow(2, semitones / 12);
}

export function exportFilename(eqActive: boolean, abLoop: boolean, loops: number): string {
  let name = "MyTrack-150pct-+2st";
  if (abLoop) name += `-loop${Math.max(1, Math.floor(loops))}x`;
  if (eqActive) name += "_eq";
  return name + ".m4a";
}

// ---------- page wiring ----------

function el<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node) throw new Error(`#${id} missing`);
  return node as T;
}

function renderFeatures(): void {
  const grid = el("featureGrid");
  grid.innerHTML = "";
  for (const f of FEATURES) {
    const div = document.createElement("div");
    div.className = "card";
    const h = document.createElement("h3");
    h.textContent = f.title;
    const p = document.createElement("p");
    p.textContent = f.body;
    div.append(h, p);
    grid.append(div);
  }
}

function renderTempoPresets(onPick: (v: number) => void): void {
  const box = el("tempoPresets");
  for (const p of [50, 75, 100, 125, 150, 200]) {
    const b = document.createElement("button");
    b.type = "button";
    b.textContent = `${p}%`;
    b.onclick = () => onPick(p);
    box.append(b);
  }
}

function updateTempoDemo(): void {
  const lenRaw = el<HTMLInputElement>("trackLen").value;
  const tempo = Number(el<HTMLInputElement>("tempo").value);
  el("tempoOut").textContent = `${tempo}%`;
  const secs = parseLength(lenRaw);
  el("tempoResult").textContent =
    secs == null
      ? "Enter a length like 3:45 or 225"
      : `Plays as ${formatTime(effectiveDuration(secs, tempo))} @ ${tempo}% (was ${formatTime(secs)})`;
}

function updatePitchDemo(): void {
  const st = Number(el<HTMLInputElement>("semi").value);
  el("semiOut").textContent = `${st.toFixed(1)} st`;
  el("pitchResult").textContent = `Frequency ratio ×${semitoneRatio(st).toFixed(4)} — tempo unchanged`;
}

function updateFileDemo(): void {
  const eq = el<HTMLInputElement>("eqOn").checked;
  const ab = el<HTMLInputElement>("abOn").checked;
  const n = Number(el<HTMLInputElement>("loopN").value) || 1;
  el("fileResult").textContent = exportFilename(eq, ab, n);
}

function drawWave(): void {
  const canvas = el<HTMLCanvasElement>("waveCanvas");
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  const { width: W, height: H } = canvas;
  ctx.clearRect(0, 0, W, H);
  // loop region
  ctx.fillStyle = "rgba(10, 132, 255, 0.18)";
  ctx.fillRect(W * 0.2, 0, W * 0.45, H);
  // fake waveform bars
  for (let x = 0; x < W; x += 4) {
    const inLoop = x > W * 0.2 && x < W * 0.65;
    const a = Math.abs(Math.sin(x * 0.08) * 0.6 + Math.sin(x * 0.023) * 0.4);
    const h = 8 + a * (H - 20);
    ctx.fillStyle = inLoop ? "#0a84ff" : "rgba(120,120,128,0.55)";
    ctx.fillRect(x, (H - h) / 2, 2.5, h);
  }
  // playhead
  ctx.fillStyle = "#ff453a";
  ctx.fillRect(W * 0.38, 0, 2, H);
}

async function loadRelease(): Promise<void> {
  const line = el("releaseLine");
  const btn = el<HTMLAnchorElement>("downloadBtn");
  try {
    const res = await fetch(API_LATEST, { headers: { Accept: "application/vnd.github+json" } });
    if (!res.ok) throw new Error(String(res.status));
    const data = (await res.json()) as { tag_name?: string; html_url?: string };
    if (data.tag_name) {
      line.textContent = `Latest: ${data.tag_name} — see what's new`;
      btn.href = data.html_url ?? RELEASES_URL;
      btn.textContent = `Download ${data.tag_name}`;
      return;
    }
    throw new Error("no tag");
  } catch {
    line.textContent = "Releases live on GitHub — stable + opt-in beta channel in-app.";
    btn.href = RELEASES_URL;
  }
}

function main(): void {
  renderFeatures();
  const tempoInput = el<HTMLInputElement>("tempo");
  renderTempoPresets((v) => {
    tempoInput.value = String(v);
    updateTempoDemo();
  });
  for (const id of ["trackLen", "tempo"]) {
    el(id).addEventListener("input", updateTempoDemo);
  }
  el("semi").addEventListener("input", updatePitchDemo);
  for (const id of ["eqOn", "abOn", "loopN"]) {
    el(id).addEventListener("input", updateFileDemo);
  }
  updateTempoDemo();
  updatePitchDemo();
  updateFileDemo();
  drawWave();
  void loadRelease();
}

main();
