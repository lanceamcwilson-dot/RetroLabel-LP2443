"""End-to-end: drive the running app over HTTP, then decode the barcode from (a) the preview PNG and (b) the EPL2 bytes that would go to the printer."""
import json, re, sys, time, urllib.request, io, os, glob
from PIL import Image
import zxingcpp
BASE = "http://127.0.0.1:47500"
H = {"X-ZLP": "1"}
def call(method, path, body=None, headers=None, raw=False):
    req = urllib.request.Request(BASE + path, data=body, method=method, headers={**H, **(headers or {})})
    with urllib.request.urlopen(req) as r:
        d = r.read()
        return d if raw else json.loads(d)
def upload(name):
    data = open(os.path.join(os.path.dirname(__file__), "data", name), "rb").read()
    return call("POST", "/api/docs", data, {"X-File-Name": name, "Content-Type": "application/pdf"})
def put_settings(**kw):
    s = call("GET", "/api/state")["settings"]; s.update(kw)
    return call("PUT", "/api/settings", json.dumps(s).encode(), {"Content-Type": "application/json"})["settings"]
def decode(img):
    return [(r.format.name, r.text) for r in zxingcpp.read_barcodes(img)]
def epl_to_image(b, w=812, h=1218, zero_black=True):
    img = Image.new("1", (w, h), 1)  # 1 = white
    px = img.load(); pos = 0
    while True:
        i = b.find(b"\nGW", pos)
        if i < 0: break
        i += 1; c = 0; j = i
        while c < 4:
            j += 1
            if b[j:j+1] == b",": c += 1
        x0, y0, bw, rows = [int(t) for t in b[i+2:j].decode().split(",")]
        data = b[j+1:j+1+bw*rows]
        for r in range(rows):
            for bx in range(bw):
                v = data[r*bw+bx]
                if zero_black: v ^= 0xFF
                for bit in range(8):
                    x = bx*8+bit
                    if x < w and v & (0x80 >> bit): px[x, y0+r] = 0
        pos = j + bw*rows
    return img
ok = True
def check(name, cond, detail=""):
    global ok; ok &= bool(cond); print(("PASS " if cond else "FAIL ") + name, detail if not cond else "")

put_settings(printerName="FILE:/tmp/zlp-e2e/epl", backend="RawEpl2")
for f, expect in [("label_4x6.pdf", "9400111899223456789012"), ("label_6x4_landscape.pdf", "LANDSCAPE123")]:
    d = upload(f)
    png = call("GET", f"/api/docs/{d['id']}/preview/0.png", raw=True)
    img = Image.open(io.BytesIO(png)).convert("L")
    img.save(f"/tmp/zlp-e2e/prev_{f}.png")
    res = decode(img)
    check(f"{f}: barcode decodes from the 1-bit preview", any(t == expect for _, t in res), str(res))
    before = len(glob.glob("/tmp/zlp-e2e/epl/*.epl"))
    call("POST", "/api/print", json.dumps({"docId": d["id"], "copies": 1}).encode(), {"Content-Type": "application/json"})
    for _ in range(80):
        time.sleep(0.1)
        if len(glob.glob("/tmp/zlp-e2e/epl/*.epl")) > before: break
    newest = max(glob.glob("/tmp/zlp-e2e/epl/*.epl"), key=os.path.getmtime)
    b = open(newest, "rb").read()
    check(f"{f}: EPL2 file written", b.startswith(b"\nN\nq812\nQ1218,24\n"))
    img2 = epl_to_image(b)
    res2 = decode(img2.convert("L"))
    check(f"{f}: barcode decodes from the EPL2 bytes (what the printer would burn)", any(t == expect for _, t in res2), str(res2))
    print("   ", len(b), "bytes of EPL2")

# also at 50% and 4x/1x smoothing
d = upload("label_4x6.pdf")
for ss in (1, 2, 4):
    put_settings(supersample=ss, scalePercent=100)
    img = Image.open(io.BytesIO(call("GET", f"/api/docs/{d['id']}/preview/0.png?x={ss}", raw=True))).convert("L")
    check(f"supersample {ss}x: barcode decodes", any(t == "9400111899223456789012" for _, t in decode(img)))
put_settings(supersample=4)
sys.exit(0 if ok else 1)
