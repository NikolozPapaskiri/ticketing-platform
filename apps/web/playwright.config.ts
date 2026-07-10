import { defineConfig, devices } from "@playwright/test";

const baseURL = process.env.WEB_BASE_URL ?? "http://localhost:3000";
const webUrl = new URL(baseURL);
const webPort = webUrl.port || "3000";

export default defineConfig({
  testDir: "./tests",
  fullyParallel: false,
  timeout: 60_000,
  expect: {
    timeout: 10_000
  },
  use: {
    baseURL,
    trace: "retain-on-failure"
  },
  webServer: process.env.PLAYWRIGHT_SKIP_WEB_SERVER
    ? undefined
    : {
        command: process.env.PLAYWRIGHT_WEB_SERVER_COMMAND ?? `npm run dev -- -p ${webPort}`,
        url: baseURL,
        reuseExistingServer: true,
        timeout: 120_000
      },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] }
    }
  ]
});
