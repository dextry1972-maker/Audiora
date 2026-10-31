r"""Analiza una sesión de escucha real de Audiora (solo lectura): métricas por sesión, acumulación de latencia,
eventos del diagnóstico y comparación con el historial propio.  No modifica nada.

Uso:
  python tools/analizar-sesion-escucha.py --desde 2026-10-31T03:10 [--hasta 2026-10-31T03:40] [--cortes 03:17:20,03:24:05]

  --desde/--hasta   ventana de tiempo local (ISO); por defecto hasta ahora
  --cortes          horas (HH:MM:SS) en que el usuario oyó un corte; se muestran las métricas ±10 s alrededor
Lee %LOCALAPPDATA%\SonosFlow\logs (stream-*.csv y diag-*.jsonl).  Los umbrales son heurísticos, no veredictos.
"""
import argparse, datetime as dt, glob, io, json, os, re, statistics, sys

sys.stdout.reconfigure(encoding="utf-8")
LOGS = os.path.expandvars(r"%LOCALAPPDATA%\SonosFlow\logs")

def num(d, k, default=None):
    try: return float(d[k])
    except Exception: return default

def load_csv(path):
    hdr, rows, notes = None, [], []
    for line in io.open(path, encoding="utf-8", errors="replace"):
        line = line.rstrip("\n")
        if line.startswith("# "): notes.append(line[2:]); continue
        if hdr is None: hdr = line.split(","); continue
        p = line.split(",")
        if len(p) >= len(hdr): rows.append(dict(zip(hdr, p)))
    start = dt.datetime.strptime(re.search(r"stream-(\d{8}-\d{6})", path).group(1), "%Y%m%d-%H%M%S")
    meta = next((n for n in notes if "target=" in n), "")
    margin = re.search(r"margen=(\d+)", meta)
    source = re.search(r"capture=([^;]+)", meta)
    return start, rows, notes, (int(margin.group(1)) if margin else None), (source.group(1) if source else "?")

def slope_per_min(xs, ys):
    if len(xs) < 30: return None
    mx, my = statistics.mean(xs), statistics.mean(ys)
    den = sum((x - mx) ** 2 for x in xs)
    return None if den == 0 else sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / den * 60

def clock(start, t): return (start + dt.timedelta(seconds=t)).strftime("%H:%M:%S")

def analyse(path):
    start, rows, notes, margin, source = load_csv(path)
    if not rows: return None
    dur = float(rows[-1]["elapsed_s"])
    steady = [r for r in rows if float(r["elapsed_s"]) > 12]
    t = [float(r["elapsed_s"]) for r in steady]
    buf = [(float(r["elapsed_s"]), num(r, "buffer_est_s")) for r in steady if num(r, "buffer_est_s") is not None]
    back = [num(r, "backlog_ms", 0) for r in steady]
    rate = [num(r, "send_rate_kBps") for r in steady if r.get("paused", "False").lower() != "true" and num(r, "send_rate_kBps")]
    last = rows[-1]
    first_min = [b for x, b in buf if x <= 132]
    last_min = [b for x, b in buf if x >= dur - 120]
    s = dict(path=path, start=start, dur=dur, margin=margin, source=source, rows=rows, notes=notes, steady=steady)
    s["buf"] = [b for _, b in buf]
    s["buf_slope"] = slope_per_min([x for x, _ in buf], [b for _, b in buf])
    s["buf_first"] = statistics.median(first_min) if first_min else None
    s["buf_last"] = statistics.median(last_min) if last_min else None
    s["back_max"] = max(back) if back else 0
    s["back_p99"] = sorted(back)[int(len(back) * 0.99)] if back else 0
    s["rate"] = rate
    s["dropped"] = num(last, "dropped_chunks", 0); s["fill_kB"] = num(last, "silence_kB", 0)
    s["recon"] = num(last, "reconnects", 0); s["gaps"] = num(last, "capture_gaps", 0)
    s["nonplay"] = [float(r["elapsed_s"]) for r in rows if r.get("transport", "PLAYING").strip() not in ("PLAYING", "TRANSITIONING", "")]
    s["stalls"] = [n for n in notes if "atasco" in n]
    s["disc"] = [n for n in notes if "desconexi" in n]
    s["watchdog"] = [n for n in notes if "vigilante" in n or "reintento" in n]
    mem = [(float(r["elapsed_s"]), num(r, "private_memory_mb")) for r in rows if num(r, "private_memory_mb")]
    cpu = [num(r, "process_cpu_pct") for r in rows if num(r, "process_cpu_pct") is not None]
    s["mem"] = (mem[0][1], mem[-1][1], max(m for _, m in mem)) if mem else None
    s["cpu"] = statistics.mean(cpu) if cpu else None
    return s

def report(s, cuts):
    print(f"== {os.path.basename(s['path'])}  inicio {s['start']:%H:%M:%S}  duración {s['dur']/60:.1f} min  margen={s['margin']} ms  fuente={s['source']}")
    b = s["buf"]
    if b:
        q = sorted(b)
        print(f"   búfer del Sonos (ESTIMACIÓN ±1 s): min={q[0]:.2f}  p5={q[len(q)//20]:.2f}  mediana={statistics.median(b):.2f}  max={q[-1]:.2f} s")
        if s["buf_first"] is not None and s["buf_last"] is not None:
            print(f"   acumulación de latencia: mediana primeros 2 min {s['buf_first']:.2f} s → últimos 2 min {s['buf_last']:.2f} s (Δ {s['buf_last']-s['buf_first']:+.2f} s); tendencia {s['buf_slope']:+.3f} s/min" if s["buf_slope"] is not None else "   acumulación de latencia: datos insuficientes")
    print(f"   cola del servidor: máx {s['back_max']:.0f} ms, p99 {s['back_p99']:.0f} ms | ritmo kB/s: " + (f"min {min(s['rate']):.0f}  mediana {statistics.median(s['rate']):.0f}  max {max(s['rate']):.0f}" if s["rate"] else "n/d"))
    print(f"   desbordes/descartes={s['dropped']:.0f}  relleno del marcapasos={s['fill_kB']:.0f} kB  reconexiones={s['recon']:.0f}  huecos de captura={s['gaps']:.0f}  s fuera de PLAYING={len(s['nonplay'])}  atascos de red={len(s['stalls'])}  desconexiones={len(s['disc'])}")
    if s["mem"]: print(f"   recursos: memoria privada {s['mem'][0]:.0f}→{s['mem'][1]:.0f} MB (pico {s['mem'][2]:.0f}); CPU media {s['cpu']:.2f} %" if s["cpu"] is not None else f"   memoria {s['mem']}")
    for n in s["stalls"] + s["disc"] + s["watchdog"]: print("     ·", n[:150])
    # diagnostics file of the same session (same minute)
    stamp = s["start"].strftime("%Y%m%d-%H%M")
    for f in sorted(glob.glob(os.path.join(LOGS, f"diag-{stamp}*.jsonl"))):
        lines = [json.loads(l) for l in io.open(f, encoding="utf-8")]
        eps = [(l["t"], l["d"]) for l in lines if l.get("k") in ("episodio", "anomalia")]
        s10 = [l["d"] for l in lines if l.get("k") == "resumen10s"]
        fin = next((l["d"] for l in lines if l.get("k") == "resumen"), "(sin resumen: sesión no cerrada)")
        print(f"   diagnóstico {os.path.basename(f)}: {len(lines)} líneas; escritura máx {max((x.get('escritura_max_ms',0) for x in s10), default=0)} ms; hueco tras audio máx {max((x.get('hueco_tras_audio_max_ms',0) for x in s10), default=0)} ms; proceso máx {max((x.get('proceso_max_ms',0) for x in s10), default=0)} ms")
        for tt, d in eps[:12]: print(f"     episodio/anomalía a las {clock(s['start'], tt)}: {d}")
        print(f"   veredicto del diagnóstico: {fin}")
    for c in cuts:
        h, m, sec = map(int, c.split(":")); tc = (s["start"].replace(hour=h, minute=m, second=sec) - s["start"]).total_seconds()
        if not (0 <= tc <= s["dur"]): continue
        win = [r for r in s["rows"] if abs(float(r["elapsed_s"]) - tc) <= 10]
        bb = [num(r, "buffer_est_s") for r in win if num(r, "buffer_est_s") is not None]
        print(f"   ► corte oído a las {c}: búfer est. min {min(bb) if bb else 'n/d'} | cola máx {max((num(r,'backlog_ms',0) for r in win), default=0):.0f} ms | ritmo mín {min((num(r,'send_rate_kBps',999) for r in win), default=0):.0f} kB/s | relleno Δ {num(win[-1],'silence_kB',0)-num(win[0],'silence_kB',0):.0f} kB | transporte {sorted(set(r['transport'] for r in win))}" if win else f"   ► corte a las {c}: sin filas")
    print()

def baseline(before):
    groups = {}
    for f in sorted(glob.glob(os.path.join(LOGS, "stream-*.csv"))):
        st = re.search(r"stream-(\d{8}-\d{6})", f)
        if dt.datetime.strptime(st.group(1), "%Y%m%d-%H%M%S") >= before: continue
        a = analyse(f)
        if a is None or a["dur"] < 300: continue
        groups.setdefault(a["margin"], []).append(a)
    print("--- Historial propio (sesiones ≥ 5 min anteriores a esta) ---")
    for m, lst in sorted(groups.items(), key=lambda kv: (kv[0] is None, kv[0])):
        hours = sum(a["dur"] for a in lst) / 3600
        bufs = [x for a in lst for x in a["buf"]]
        print(f"margen={m} ms: {len(lst)} sesiones, {hours:.1f} h | atascos de red/h={sum(len(a['stalls']) for a in lst)/hours:.2f} | reconexiones/h={sum(a['recon'] for a in lst)/hours:.2f} | descartes={sum(a['dropped'] for a in lst):.0f} | huecos captura={sum(a['gaps'] for a in lst):.0f} | búfer est. mediana={statistics.median(bufs) if bufs else float('nan'):.2f} s, min={min(bufs) if bufs else float('nan'):.2f} s")
    print("(Una sola sesión de escucha no demuestra una mejora estadística: compara tasas por hora y búfer, no un cero aislado.)\n")

ap = argparse.ArgumentParser()
ap.add_argument("--desde", required=True); ap.add_argument("--hasta"); ap.add_argument("--cortes", default="")
a = ap.parse_args()
t0 = dt.datetime.fromisoformat(a.desde); t1 = dt.datetime.fromisoformat(a.hasta) if a.hasta else dt.datetime.now()
cuts = [c for c in a.cortes.split(",") if c]
files = [f for f in sorted(glob.glob(os.path.join(LOGS, "stream-*.csv"))) if t0 <= dt.datetime.strptime(re.search(r"stream-(\d{8}-\d{6})", f).group(1), "%Y%m%d-%H%M%S") <= t1]
print(f"Ventana {t0:%Y-%m-%d %H:%M} → {t1:%H:%M}: {len(files)} sesión(es)\n")
sessions = [s for s in (analyse(f) for f in files) if s]
for s in sessions: report(s, cuts)
tot = sum(s["dur"] for s in sessions)
print(f"TOTAL en la ventana: {tot/60:.1f} min de transmisión; atascos={sum(len(s['stalls']) for s in sessions)}; reconexiones={sum(s['recon'] for s in sessions):.0f}; descartes={sum(s['dropped'] for s in sessions):.0f}; huecos de captura={sum(s['gaps'] for s in sessions):.0f}\n")
baseline(t0)
