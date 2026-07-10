import { chromium } from 'playwright';
const BASE = process.env.BASE || 'http://localhost:8088';
const OUT = process.env.OUT || `${process.cwd()}/shots`;
import { mkdirSync } from 'node:fs';
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const ctx = await browser.newContext({
  baseURL: BASE,
  viewport: { width: 1480, height: 940 },
  deviceScaleFactor: 2, // crisp retina-ish screenshots for the README
});

// Login via API, then plant the session the same way the app does (cookie).
const login = await ctx.request.post('/api/auth/login', { form: { email: 'superadmin@acme.com', password: 'secret' } });
if (!login.ok()) { console.log('LOGIN HTTP', login.status()); process.exit(2); }
const tok = (await login.json()).token;
const api = (p) => ctx.request.get(p, { headers: { authorization: `Bearer ${tok}` } }).then(r => r.json());
const people = await api('/api/customers/people');
const companies = await api('/api/customers/companies');
const personId = people.items[0].id;
const companyId = (companies.items.find(c => c.display_name === 'Brightside Solar') || companies.items[0]).id;
console.log('login ok; person', personId, 'company', companyId);

const page = await ctx.newPage();
page.on('pageerror', e => console.log('  [pageerror]', e.message));

// Dismiss the demo + cookie banners once (persisted, so they stay gone for later navigations).
await page.goto('/backend', { waitUntil: 'domcontentloaded', timeout: 30000 });
await page.waitForTimeout(1500);
for (const label of ['Dismiss', 'Accept cookies']) {
  const btn = page.getByRole('button', { name: label, exact: false }).first();
  if (await btn.count().catch(() => 0)) await btn.click().catch(() => {});
  await page.waitForTimeout(300);
}
// The demo banner's close (×) button.
await page.locator('button:has-text("×"), [aria-label="Close"], [aria-label="Dismiss"]').first().click().catch(() => {});
await page.waitForTimeout(500);

// Remove the demo-instance banner + floating feedback widget so screenshots are clean.
async function declutter() {
  await page.evaluate(() => {
    const kill = (el) => el && el.remove();
    for (const sel of ['[role="status"]', '[class*="toast" i]', '[class*="sonner" i]', 'li[data-sonner-toast]', '[data-radix-toast-viewport]']) {
      document.querySelectorAll(sel).forEach(kill);
    }
    for (const el of Array.from(document.querySelectorAll('body *'))) {
      const t = (el.textContent || '');
      if (t.includes('Demo Environment') && t.length < 400) { kill(el.closest('div')); }
      if (/Failed to load record lock/.test(t) && t.length < 120) kill(el.closest('div,li'));
      if (/^\s*Feedback\s*$/.test(t) && el.children.length === 0) kill(el.closest('button,a,div'));
    }
  }).catch(() => {});
}

async function shot(path, file, { marker, full = false, settle = 1600 } = {}) {
  try {
    await page.goto(path, { waitUntil: 'domcontentloaded', timeout: 30000 });
    if (marker) await page.getByText(marker, { exact: false }).first().waitFor({ timeout: 20000 }).catch(() => {});
    await page.waitForTimeout(settle);
    await declutter();
    await page.waitForTimeout(200);
    await page.screenshot({ path: `${OUT}/${file}`, fullPage: full });
    console.log('  ✓', file);
  } catch (e) { console.log('  ✗', file, '-', e.message.split('\n')[0]); }
}

await shot('/backend', 'dashboard.png', { marker: 'Dashboard', full: false });
await shot('/backend/customers/people', 'people-list.png', { marker: 'Mia Johnson', full: true });
await shot(`/backend/customers/people-v2/${personId}`, 'person-detail.png', { marker: 'Johnson', full: true });
await shot('/backend/customers/companies', 'companies-list.png', { marker: 'Brightside Solar', full: true });
await shot(`/backend/customers/companies-v2/${companyId}`, 'company-detail.png', { marker: 'Brightside Solar', full: true });
await shot('/backend/customers/deals', 'deals-pipeline.png', { marker: 'Deal', full: false });
// Deals kanban board (click the Kanban tab).
try {
  await page.goto('/backend/customers/deals', { waitUntil: 'domcontentloaded', timeout: 30000 });
  await page.getByRole('tab', { name: 'Kanban' }).or(page.getByText('Kanban', { exact: true })).first().click({ timeout: 15000 });
  await page.waitForTimeout(2500);
  await declutter();
  await page.waitForTimeout(200);
  await page.screenshot({ path: `${OUT}/deals-kanban.png`, fullPage: false });
  console.log('  ✓ deals-kanban.png');
} catch (e) { console.log('  ✗ deals-kanban.png -', e.message.split('\n')[0]); }

await browser.close();
console.log('done ->', OUT);
