export function TransitMark({ className = "" }: { className?: string }) {
  return (
    <img className={`transit-mark ${className}`} src="/favicon.svg" width="48" height="48" alt="" aria-hidden="true" />
  );
}

export function TransitBrand() {
  return (
    <span className="transit-brand">
      <TransitMark />
      <span className="transit-wordmark"><strong>Aspireland</strong><span>Transit</span></span>
    </span>
  );
}
