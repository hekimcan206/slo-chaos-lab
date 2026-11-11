// Captures the provisioned Grafana SLO dashboard for one experiment's time range, so each postmortem shows what an
// on-call engineer would have seen. Env: GRAFANA_URL, FROM_MS, TO_MS, OUT (file name), OUT_DIR.
import { chromium } from "playwright";

const grafana = process.env.GRAFANA_URL ?? "http://grafana:3000";
const from = process.env.FROM_MS;
const to = process.env.TO_MS;
const out = `${process.env.OUT_DIR ?? "/out"}/${process.env.OUT ?? "dashboard.png"}`;
if (!from || !to) throw new Error("FROM_MS and TO_MS are required");

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1600, height: 1650 }, deviceScaleFactor: 1, colorScheme: "light" });
await page.goto(`${grafana}/d/slo-ledger/ledger-posting-slos?orgId=1&from=${from}&to=${to}&timezone=utc&kiosk&theme=light&refresh=`);
await page.waitForSelector("[data-testid='data-testid panel content'], .panel-content, section", { timeout: 30000 });
await page.waitForLoadState("networkidle");
await page.waitForTimeout(10000); // let every panel finish its query and paint
// No fullPage: resizing the viewport for a full-page shot makes the chart canvases clear and redraw after the capture.
await page.screenshot({ path: out });
await browser.close();
console.log(`wrote ${out}`);
