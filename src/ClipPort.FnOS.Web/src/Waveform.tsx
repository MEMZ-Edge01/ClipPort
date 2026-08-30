export function Waveform({
  title,
  byteRates = [],
  itemRates = [],
  positions = [],
  emptyText,
}: {
  title: string;
  byteRates?: number[];
  itemRates?: number[];
  positions?: number[];
  emptyText: string;
}) {
  const length = Math.min(byteRates.length, itemRates.length, positions.length);
  if (length === 0) {
    return <section className="waveform"><h3>{title}</h3><p>{emptyText}</p></section>;
  }
  const bytes = byteRates.slice(0, length);
  const items = itemRates.slice(0, length);
  const progress = positions.slice(0, length);
  const bytePeak = Math.max(1, ...bytes);
  const itemPeak = Math.max(1, ...items);
  const points = (values: number[], peak: number) => values.map((value, index) => {
    const x = Math.max(0, Math.min(1, progress[index] ?? index / Math.max(1, length - 1))) * 100;
    const y = 38 - Math.max(0, Math.min(1, value / peak)) * 34;
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  }).join(' ');
  return <section className="waveform" aria-label={title}>
    <h3>{title}</h3>
    <svg viewBox="0 0 100 42" role="img" aria-label={title} preserveAspectRatio="none">
      <path d="M0 38H100" className="wave-grid" />
      <polyline points={points(bytes, bytePeak)} className="wave-bytes" />
      <polyline points={points(items, itemPeak)} className="wave-items" />
    </svg>
    <div className="wave-legend"><span className="byte-legend">B/s</span><span className="item-legend">files/s</span></div>
  </section>;
}
