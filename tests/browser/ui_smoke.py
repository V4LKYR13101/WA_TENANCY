"""Browser smoke test against an isolated local server with workers disabled.
Run with: python tests/browser/ui_smoke.py
Never point this at a production database: it creates disposable QA accounts.
"""
from pathlib import Path
import json
import re
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "artifacts" / "browser"
OUTPUT.mkdir(parents=True, exist_ok=True)
BASE = "http://127.0.0.1:5188"
errors = []

with sync_playwright() as p:
    browser = p.chromium.launch(channel="msedge", headless=True)
    page = browser.new_page(viewport={"width": 1440, "height": 1050}, device_scale_factor=1)
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(BASE + "/setup")
    if page.url.endswith("/setup"):
        expect(page.get_by_role("heading", name="Create your administrator")).to_be_visible()
        page.screenshot(path=str(OUTPUT / "setup.png"), full_page=True)
        page.get_by_label("Administrator email").fill("qa@example.test")
        page.get_by_label(re.compile("^Password")).fill("UiTestPassword!2026")
        page.get_by_label("Confirm password").fill("UiTestPassword!2026")
        page.get_by_role("button", name="Create administrator").click()
    else:
        page.get_by_label("Email address").fill("qa@example.test")
        page.get_by_label("Password", exact=True).fill("UiTestPassword!2026")
        page.get_by_role("button", name="Sign in", exact=False).click()
    expect(page.get_by_role("heading", name="Company workspace.")).to_be_visible()
    page.locator('header[data-interactive="ready"]').wait_for()
    if page.get_by_role("heading", name="Your companies start here").is_visible():
        page.get_by_label("Company name", exact=True).fill("Acme Industries")
        page.get_by_label("Company slug", exact=True).fill("acme-industries")
        page.get_by_role("button", name="Create company", exact=True).click()
        expect(page.get_by_role("heading", name="WhatsApp connections")).to_be_visible()
    page.get_by_label("Number label").fill("Sales · Johannesburg")
    page.get_by_role("button", name="Add number", exact=False).click()
    expect(page.get_by_role("heading", name="Sales · Johannesburg")).to_be_visible()
    page.screenshot(path=str(OUTPUT / "numbers.png"), full_page=True)
    page.get_by_role("button", name="Connect number", exact=True).first.click()
    expect(page.get_by_role("alert")).to_contain_text("Workers are disabled")
    page.get_by_role("tab", name="Team", exact=True).click()
    page.get_by_label("Email", exact=True).fill("agent@example.test")
    page.get_by_label("Initial password").fill("AgentTestPassword!2026")
    page.get_by_label("Role").select_option("Agent")
    page.get_by_role("button", name="Add member", exact=True).click()
    expect(page.get_by_role("cell", name="agent@example.test")).to_be_visible()
    page.get_by_role("tab", name="API keys", exact=True).click()
    page.get_by_label("Key label").fill("CRM integration")
    page.get_by_role("button", name="Create key", exact=True).click()
    expect(page.get_by_label("New API key")).to_have_value(re.compile(r"wat_[0-9a-f]{64}"))
    # Secrets are intentionally not included in saved screenshots.
    page.get_by_role("tab", name="Webhooks", exact=True).click()
    page.get_by_label("Webhook URL").fill("https://127.0.0.1/events")
    page.get_by_role("button", name="Save profile", exact=True).click()
    expect(page.get_by_role("alert")).to_contain_text("public")
    page.get_by_label("Webhook URL").fill("https://example.com/events")
    page.get_by_role("button", name="Save profile", exact=True).click()
    expect(page.get_by_role("status")).to_contain_text("Webhook profile saved")
    page.screenshot(path=str(OUTPUT / "webhooks.png"), full_page=True)
    page.get_by_role("tab", name="Inbox", exact=True).click()
    expect(page.get_by_role("heading", name="No messages here yet")).to_be_visible()
    page.get_by_role("tab", name="Send", exact=True).click()
    expect(page.get_by_role("button", name="Queue message", exact=False)).to_be_disabled()
    page.get_by_role("link", name="API & webhooks").click()
    expect(page.get_by_role("heading", name="One API. Your companies.")).to_be_visible()
    page.screenshot(path=str(OUTPUT / "api-guide.png"), full_page=True)
    page.goto(BASE + "/")
    page.locator('header[data-interactive="ready"]').wait_for()
    page.set_viewport_size({"width": 390, "height": 844})
    expect(page.get_by_role("heading", name="WhatsApp connections")).to_be_visible()
    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth"), "Mobile layout overflows"
    page.screenshot(path=str(OUTPUT / "mobile.png"), full_page=True)
    page.get_by_role("button", name="Sign out", exact=False).click()
    expect(page.get_by_role("heading", name="Sign in to your workspace")).to_be_visible()
    page.get_by_label("Email address").fill("agent@example.test")
    page.get_by_label("Password", exact=True).fill("AgentTestPassword!2026")
    page.get_by_role("button", name="Sign in", exact=False).click()
    expect(page.get_by_role("heading", name="Company workspace.")).to_be_visible()
    page.locator('header[data-interactive="ready"]').wait_for()
    expect(page.get_by_role("tab", name="Webhooks", exact=True)).to_have_count(0)
    expect(page.get_by_role("tab", name="Team", exact=True)).to_have_count(0)
    expect(page.get_by_role("button", name="Add number", exact=False)).to_have_count(0)
    assert not errors, errors
    browser.close()
print(json.dumps({"result": "passed", "browser": "Microsoft Edge headless", "screenshots": str(OUTPUT), "page_errors": errors}))
