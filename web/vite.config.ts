import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const port = Number(env.PORT || 5173);
  const apiUrl = env.API_URL || "http://localhost:5000";
  const isOperations = env.VITE_SITE === "operations";
  const operatorKey = process.env.OPERATOR_KEY;
  const proxyHeaders = isOperations && operatorKey ? { "X-Operator-Key": operatorKey } : undefined;

  return {
    plugins: [react()],
    server: {
      host: "0.0.0.0",
      port,
      strictPort: true,
      proxy: {
        "/api": {
          target: apiUrl,
          changeOrigin: true,
          headers: proxyHeaders,
        },
      },
    },
    preview: {
      host: "0.0.0.0",
      port,
      strictPort: true,
    },
  };
});
