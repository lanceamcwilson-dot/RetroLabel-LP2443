import json, os, re, sys, time
from playwright.sync_api import sync_playwright
OUT = "/tmp/zlp-shots"; os.makedirs(OUT, exist_ok=True)
B = "http://127.0.0.1:47500"
D = os.path.join(os.path.dirname(__file__), "data")
errors = []
with sync_playwright() as p:
    br = p.chromium.launch()
    ctx = br.new_context(viewport={"width": 1180, "height": 820}, device_scale_factor=1)
    pg = ctx.new_page()
    pg.on("console", lambda m: errors.append(("console", m.type, m.text)) if m.type in ("error", "warning") else None)
    pg.on("pageerror", lambda e: errors.append(("pageerror", str(e))))
    pg.goto(B + "/"); pg.wait_for_timeout(800)
    pg.screenshot(path=f"{OUT}/01-empty.png")
    pg.set_input_files("#file", f"{D}/multi_3page.pdf"); pg.wait_for_selector("#preview[src]"); pg.wait_for_timeout(900)
    pg.screenshot(path=f"{OUT}/02-loaded.png")
    pg.click("#nextPg"); pg.wait_for_timeout(500)
    pg.set_input_files("#file", f"{D}/label_6x4_landscape.pdf"); pg.wait_for_timeout(1200)
    pg.screenshot(path=f"{OUT}/03-landscape.png")
    # toggle auto-rotate off to see crop warning
    pg.click("[data-tab=settings]"); pg.wait_for_timeout(600)
    pg.screenshot(path=f"{OUT}/04-settings.png", full_page=True)
    pg.uncheck("#s_auto"); pg.wait_for_timeout(900)
    pg.click("[data-tab=print]"); pg.wait_for_timeout(800)
    pg.screenshot(path=f"{OUT}/05-crop-warning.png")
    pg.click("[data-tab=settings]"); pg.check("#s_auto"); pg.wait_for_timeout(700)
    pg.click("[data-tab=print]"); pg.click("#printBtn"); pg.wait_for_timeout(1500)
    pg.click("[data-tab=history]"); pg.wait_for_timeout(500)
    pg.screenshot(path=f"{OUT}/06-history.png")
    pg.click("[data-tab=diag]"); pg.wait_for_timeout(900)
    pg.screenshot(path=f"{OUT}/07-diagnostics.png", full_page=True)
    # phone tab
    pg.click("[data-tab=phone]"); pg.check("#r_enable"); pg.wait_for_timeout(2500)
    pg.click("#pairBtn"); pg.wait_for_selector("#qr svg"); pg.wait_for_timeout(500)
    pg.screenshot(path=f"{OUT}/08-phone.png", full_page=True)
    pair_url = pg.input_value("#pairUrl")
    code = pg.inner_text("#pairCode")
    print("pair url", pair_url, code)

    # ---- phone
    iph = p.devices["iPhone 13"]
    mctx = br.new_context(**iph)
    m = mctx.new_page()
    m.on("console", lambda x: errors.append(("m-console", x.type, x.text)) if x.type in ("error",) else None)
    m.on("pageerror", lambda e: errors.append(("m-pageerror", str(e))))
    url = pair_url.replace("http://", "http://127.0.0.1:").split("/m#")[0] if False else None
    port = re.search(r":(\d+)/m", pair_url).group(1)
    m.goto(f"http://127.0.0.1:{port}/m#pair={code}"); m.wait_for_timeout(700)
    m.screenshot(path=f"{OUT}/10-m-pair.png")
    m.click("#pairBtn"); m.wait_for_selector("#mainView:not([hidden])"); m.wait_for_timeout(700)
    m.screenshot(path=f"{OUT}/11-m-home.png")
    m.set_input_files("#file", f"{D}/label_4x6.pdf"); m.wait_for_selector("#docCard:not([hidden])"); m.wait_for_timeout(1500)
    m.screenshot(path=f"{OUT}/12-m-preview.png", full_page=True)
    m.click("#plus"); m.click("#print"); m.wait_for_timeout(2800)
    m.screenshot(path=f"{OUT}/13-m-queued.png", full_page=True)
    print("ERRORS:", json.dumps(errors, indent=1))
    br.close()
