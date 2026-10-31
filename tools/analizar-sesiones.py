r"""Analiza los stream-*.csv reales de Audiora (solo lectura): sesiones largas, anomalías por segundo
(búfer bajo, cola atrasada, ráfagas, silencio insertado, huecos de captura, descartes, reconexiones) y periodicidad de
los episodios.  Uso:  python tools/analizar-sesiones.py [segundos_mínimos=300]
Lee %LOCALAPPDATA%\SonosFlow\logs.  No modifica nada.  Los umbrales son heurísticos, no veredictos.
"""
import csv, glob, os, re, statistics, sys, io
from collections import Counter

LOGS = os.path.expandvars(r"%LOCALAPPDATA%\SonosFlow\logs")
MIN_S = int(sys.argv[1]) if len(sys.argv) > 1 else 300

def parse(path):
    rows, notes, header, meta = [], [], None, ""
    with io.open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            if line.startswith("# "):
                if header is None and "target=" in line: meta = line[2:]
                else: notes.append(line[2:])
                continue
            if header is None:
                header = line.split(","); continue
            parts = line.split(",")
            if len(parts) < len(header): continue
            d = dict(zip(header, parts))
            try:
                d["elapsed_s"] = float(d["elapsed_s"])
            except ValueError: continue
            rows.append(d)
    return meta, rows, notes

def num(d, k, default=0.0):
    try: return float(d.get(k, default))
    except (ValueError, TypeError): return default

def hms(s):
    s = int(s); return f"{s//3600:d}:{s%3600//60:02d}:{s%60:02d}"

sessions = []
for p in sorted(glob.glob(os.path.join(LOGS, "stream-*.csv"))):
    meta, rows, notes = parse(p)
    if not rows: continue
    dur = rows[-1]["elapsed_s"]
    if dur >= MIN_S: sessions.append((p, meta, rows, notes, dur))

print(f"Sesiones >= {MIN_S}s: {len(sessions)}\n")
all_anom_times = []   # (session, t) of anomalies, for periodicity
for p, meta, rows, notes, dur in sessions:
    name = os.path.basename(p)
    margen = re.search(r"margen=(\d+)", meta); margen = int(margen.group(1)) if margen else None
    cap = re.search(r"capture=([^;]+)", meta); cap = cap.group(1) if cap else "?"
    conns = [n for n in notes if "conexi" in n.lower() and "desconex" not in n.lower()]
    disc = [n for n in notes if "desconex" in n.lower()]
    first = rows[0]
    # per-second anomalies
    anom = {"buf_low": [], "rate_hi": [], "rate_lo": [], "backlog": [], "silence": [], "gaps": [], "drop": [], "reconn": [], "notplay": []}
    prev = rows[0]
    for r in rows[1:]:
        t = r["elapsed_s"]
        if t < 12: prev = r; continue          # connection settling
        b = num(r, "buffer_est_s", -1)
        if margen and b >= 0 and b < 0.35 * margen / 1000: anom["buf_low"].append(t)
        rate = num(r, "send_rate_kBps")
        if rate > 215: anom["rate_hi"].append(t)
        if 0 < rate < 170 and r.get("paused", "False").lower() != "true": anom["rate_lo"].append(t)
        if num(r, "backlog_ms") > 400: anom["backlog"].append(t)
        if num(r, "silence_kB") > num(prev, "silence_kB") + 0.5: anom["silence"].append(t)
        if num(r, "capture_gaps") > num(prev, "capture_gaps"): anom["gaps"].append(t)
        if num(r, "dropped_chunks") > num(prev, "dropped_chunks"): anom["drop"].append(t)
        if num(r, "reconnects") > num(prev, "reconnects"): anom["reconn"].append(t)
        if r.get("transport", "PLAYING").strip() not in ("PLAYING", "TRANSITIONING", ""): anom["notplay"].append(t)
        prev = r
    last = rows[-1]
    print(f"== {name}  dur={hms(dur)}  margen={margen}ms  fuente={cap}")
    print(f"   conexiones del Sonos={len(conns)} desconexiones={len(disc)}  reconexiones(final)={num(last,'reconnects'):.0f}  descartes(final)={num(last,'dropped_chunks'):.0f}  silencio_kB(final)={num(last,'silence_kB'):.0f}  huecos_captura(final)={num(last,'capture_gaps'):.0f}")
    bs = [num(r, "buffer_est_s", -1) for r in rows if r["elapsed_s"] > 12 and num(r, "buffer_est_s", -1) >= 0]
    bl = [num(r, "backlog_ms") for r in rows if r["elapsed_s"] > 12]
    rt = [num(r, "send_rate_kBps") for r in rows if r["elapsed_s"] > 12 and num(r, "send_rate_kBps") > 0]
    if bs: print(f"   buffer_est_s: min={min(bs):.2f} p5={sorted(bs)[len(bs)//20]:.2f} mediana={statistics.median(bs):.2f} max={max(bs):.2f}")
    if bl: print(f"   backlog_ms: max={max(bl):.0f} p99={sorted(bl)[int(len(bl)*0.99)]:.0f}")
    if rt: print(f"   ritmo kB/s: mediana={statistics.median(rt):.0f} min={min(rt):.0f} max={max(rt):.0f}")
    for k, v in anom.items():
        if v: print(f"   anomalía {k}: {len(v)} s  primeros t={[hms(x) for x in v[:6]]}")
    if len(conns) > 1:
        ts = []
        for n in conns:
            m = re.match(r"(\d\d):(\d\d):(\d\d)", n)
            if m: ts.append(int(m.group(1))*3600 + int(m.group(2))*60 + int(m.group(3)))
        gaps = [b - a for a, b in zip(ts, ts[1:])]
        print(f"   intervalos entre conexiones (s): {gaps[:20]}")
    for k in ("buf_low", "backlog", "rate_hi", "silence", "gaps", "drop"):
        for t in anom[k]: all_anom_times.append((name, k, t))
    print()

# periodicity: bucket anomaly start times (cluster within 5 s), then histogram of intervals between clusters
def clusters(ts, gap=8):
    ts = sorted(ts); out = []
    for t in ts:
        if not out or t - out[-1][-1] > gap: out.append([t])
        else: out[-1].append(t)
    return [c[0] for c in out]
print("== Periodicidad de anomalías (buffer bajo / cola atrasada / ráfagas / silencio / huecos / descartes) por sesión")
for p, meta, rows, notes, dur in sessions:
    name = os.path.basename(p)
    ts = [t for (n, k, t) in all_anom_times if n == name]
    cl = clusters(ts)
    if len(cl) < 3:
        print(f"   {name}: {len(cl)} episodios (insuficientes para periodicidad)"); continue
    iv = [b - a for a, b in zip(cl, cl[1:])]
    near = [x for x in iv if 270 <= x <= 400]
    print(f"   {name}: {len(cl)} episodios; intervalos medianos={statistics.median(iv):.0f}s; entre 270-400 s: {len(near)} de {len(iv)}; primeros intervalos={[int(x) for x in iv[:10]]}")
