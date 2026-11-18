import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { OperationsApp } from "./operations/OperationsApp";
import { PassengerApp } from "./passenger/PassengerApp";
import "@fontsource/poppins/latin-400.css";
import "@fontsource/poppins/latin-500.css";
import "@fontsource/poppins/latin-600.css";
import "@fontsource/poppins/latin-700.css";
import "./brand.css";
import "./styles.css";
import "./transit.css";

const site = import.meta.env.VITE_SITE === "passenger" ? "passenger" : "operations";
document.documentElement.dataset.site = site;
document.title = site === "passenger" ? "Aspireland Transit" : "Aspireland Transit | Dispatch";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {site === "passenger" ? <PassengerApp /> : <OperationsApp />}
  </StrictMode>,
);
