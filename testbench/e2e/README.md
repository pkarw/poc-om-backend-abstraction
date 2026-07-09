# Testbench E2E (Playwright)

Verifies the ported customers pages render real data through the real OM frontend
(sidebar, people/company list + detail) against the running testbench.

```bash
cd testbench/e2e
npm install
npx playwright install chromium
node verify.mjs            # BASE=http://localhost:8088 by default
```

Logs in as superadmin (via the .NET /api/auth/login), reads seeded ids from the API,
then drives the OM pages headless and asserts the seeded people/companies + detail data
are displayed. Screenshots (people-list.png, person-detail.png, companies-list.png,
company-detail.png) are written here (gitignored).
