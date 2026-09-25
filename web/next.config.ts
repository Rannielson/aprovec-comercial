import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  output: 'standalone',
  allowedDevOrigins: ['*.localhost'],
  // The default bottom-left dev indicator overlaps the AppShell's bottom-left
  // sidebar logout button and intercepts pointer events in Playwright, so it
  // is disabled. It never renders in production (`next start`) anyway.
  devIndicators: false,
};

export default nextConfig;
