import { chromium } from 'playwright';
const BASE = process.env.BASE || 'http://localhost:8088';
const shot = (p) => `${process.cwd()}/${p}`;
let fails = 0;
const ok = (m) => console.log('  \x1b[32m✓\x1b[0m', m);
const bad = (m) => { console.log('  \x1b[31m✗\x1b[0m', m); fails++; };

const browser = await chromium.launch();
const ctx = await browser.newContext({ baseURL: BASE });
const login = await ctx.request.post('/api/auth/login', { form: { email: 'superadmin@acme.com', password: 'secret' } });
if (!login.ok()) { console.log('LOGIN HTTP', login.status()); process.exit(2); }
const tok = (await login.json()).token;
const api = (p) => ctx.request.get(p, { headers: { authorization: `Bearer ${tok}` } }).then(r => r.json());
const people = await api('/api/customers/people');
const companies = await api('/api/customers/companies');
const personId = people.items[0].id, companyId = companies.items.find(c=>c.display_name==='Brightside Solar').id;
console.log('login ok; person', personId, 'company', companyId);

const page = await ctx.newPage();
page.on('pageerror', e => console.log('  [pageerror]', e.message));
const body = async () => (await page.textContent('body')) || '';
const has = (t, s) => t.includes(s);
async function go(path, marker, label, timeout = 30000) {
  try {
    await page.goto(path, { waitUntil: 'domcontentloaded', timeout });
    if (marker) await page.getByText(marker, { exact: false }).first().waitFor({ timeout });
    await page.waitForTimeout(1200);
    return true;
  } catch (e) { bad(`${label}: ${e.message.split('\n')[0]}`); return false; }
}

console.log('— People list —');
if (await go('/backend/customers/people', 'Mia Johnson', 'people-list')) {
  await page.screenshot({ path: shot('people-list.png'), fullPage: true });
  const t = await body();
  for (const n of ['Mia Johnson','Daniel Cho','Arjun Patel','Lena Ortiz','Taylor Brooks','Naomi Harris']) (has(t,n)?ok:bad)(`list: "${n}"`);
}

console.log('— Person detail —');
if (await go(`/backend/customers/people-v2/${personId}`, 'Mia', 'person-detail')) {
  await page.screenshot({ path: shot('person-detail.png'), fullPage: true });
  const t = await body();
  (has(t,'Mia')&&has(t,'Johnson')?ok:bad)('detail: name');
  (/Operations|Director/.test(t)?ok:bad)('detail: job title');
  (has(t,'Brightside Solar')?ok:bad)('detail: linked company');
}

console.log('— Companies list —');
if (await go('/backend/customers/companies', 'Brightside Solar', 'companies-list')) {
  await page.screenshot({ path: shot('companies-list.png'), fullPage: true });
  const t = await body();
  for (const n of ['Brightside Solar','Harborview Analytics','Copperleaf']) (has(t,n)?ok:bad)(`list: "${n}"`);
}

console.log('— Company detail —');
if (await go(`/backend/customers/companies-v2/${companyId}`, 'Brightside Solar', 'company-detail')) {
  await page.screenshot({ path: shot('company-detail.png'), fullPage: true });
  const t = await body();
  (has(t,'Brightside Solar')?ok:bad)('detail: name');
  (has(t,'Renewable Energy')?ok:bad)('detail: industry');
}

await browser.close();
console.log(fails === 0 ? '\n\x1b[32mPASS\x1b[0m — all checks green' : `\n\x1b[31mFAIL\x1b[0m — ${fails} check(s) failed`);
process.exit(fails === 0 ? 0 : 1);
