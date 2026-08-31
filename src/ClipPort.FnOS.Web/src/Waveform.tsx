import { useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  alignContinuousTransition, createDisplayCoordinates, easeOutCubic,
  getWaveformDivisionStep,
} from './WaveformMath';
import type { WaveformCoordinate } from './WaveformMath';

const COPY_COLOR = '#00B7C3';
const VERIFY_COLOR = '#FFB900';
const SCALE_MULTIPLIERS = [3, 2, 1, 0] as const;
const ANIMATION_DURATION_MS = 180;
const DEFAULT_MAXIMUM_POINT_COUNT = 512;
const EMPTY_SAMPLES: number[] = [];

function useAnimatedCoordinates(target: readonly WaveformCoordinate[]) {
  const [displayed, setDisplayed] = useState<WaveformCoordinate[]>([...target]);
  const displayedRef = useRef(displayed);
  displayedRef.current = displayed;

  useEffect(() => {
    if (target.length === 0 || displayedRef.current.length === 0) {
      setDisplayed([...target]);
      return;
    }
    const start = alignContinuousTransition(displayedRef.current, target);
    let animationFrame = 0;
    let startedAt: number | undefined;
    const advance = (now: number) => {
      startedAt ??= now;
      const progress = Math.min(1, (now - startedAt) / ANIMATION_DURATION_MS);
      const eased = easeOutCubic(progress);
      setDisplayed(target.map((point, index) => ({
        x: start[index].x + (point.x - start[index].x) * eased,
        y: start[index].y + (point.y - start[index].y) * eased,
      })));
      if (progress < 1) animationFrame = requestAnimationFrame(advance);
    };
    animationFrame = requestAnimationFrame(advance);
    return () => cancelAnimationFrame(animationFrame);
  }, [target]);

  return displayed;
}

export function ThroughputChart({
  title,
  byteRates = EMPTY_SAMPLES,
  color,
  unit,
}: {
  title: string;
  byteRates?: number[];
  positions?: number[];
  color: 'copy' | 'verify';
  unit: string;
}) {
  const safeSamples = useMemo(
    () => byteRates.map(value => Number.isFinite(value) ? Math.max(0, value) : 0),
    [byteRates],
  );
  const isByteRate = unit === 'MB/s';
  const scale = isByteRate && Math.max(0, ...safeSamples) >= 1024 ** 3 ? 1024 ** 3 : isByteRate ? 1024 ** 2 : 1;
  const displayUnit = isByteRate ? scale === 1024 ** 3 ? 'GB/s' : 'MB/s' : unit;
  const peak = Math.max(0, ...safeSamples);
  const current = safeSamples.at(-1) ?? 0;
  const positiveSamples = safeSamples.filter(value => value > 0);
  const minimumPositive = positiveSamples.length > 0 ? Math.min(...positiveSamples) : 0;
  const divisionStep = getWaveformDivisionStep(peak / scale);
  const chartMaximum = Math.max(divisionStep * 4 * scale, Number.EPSILON);
  const targetCoordinates = useMemo(() => {
    if (safeSamples.length === 0) return [];
    if (safeSamples.length === 1) {
      const value = safeSamples[0];
      return [{ x: 0, y: value }, { x: 1, y: value }];
    }
    return createDisplayCoordinates(safeSamples, DEFAULT_MAXIMUM_POINT_COUNT);
  }, [safeSamples]);
  const coordinates = useAnimatedCoordinates(targetCoordinates);
  const lineColor = color === 'copy' ? COPY_COLOR : VERIFY_COLOR;
  const points = coordinates.map(point => {
    const x = point.x * 1000;
    const normalized = Math.min(1, Math.max(0, point.y / chartMaximum));
    const y = 3 + 214 * (1 - normalized);
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  }).join(' ');
  const fillPoints = points ? `0,217 ${points} 1000,217` : '';

  return <article className="throughput-card" style={{ '--chart-color': lineColor } as CSSProperties}>
    <div className="throughput-card-header">
      <span className="throughput-card-title">{title}</span>
      <span className={`throughput-card-current ${color}`}>{formatRate(current, scale, displayUnit)}</span>
    </div>
    <div className="throughput-chart-area">
      <svg className="throughput-svg" viewBox="0 0 1000 220" preserveAspectRatio="none" role="img" aria-label={title}>
        {SCALE_MULTIPLIERS.map(multiplier => {
          const y = 217 - multiplier / 4 * 214;
          return <line key={multiplier} className="throughput-grid-line" x1="0" y1={y} x2="1000" y2={y} />;
        })}
        {fillPoints && <polygon className="throughput-fill" points={fillPoints} />}
        {points && <polyline className="throughput-glow" points={points} />}
        {points && <polyline className="throughput-line" points={points} />}
      </svg>
      <div className="throughput-scale-labels">
        {SCALE_MULTIPLIERS.map(multiplier => <span key={multiplier} className="throughput-scale-label">
          {formatScaleValue(divisionStep * multiplier)}
        </span>)}
      </div>
    </div>
    <div className="throughput-card-footer">
      <div className="throughput-card-footer-left">
        <span>↑ {formatRate(peak, scale, displayUnit)}</span>
        <span>↓ {formatRate(minimumPositive, scale, displayUnit)}</span>
      </div>
      <span className="throughput-card-unit">{displayUnit}</span>
    </div>
  </article>;
}

function formatRate(value: number, scale: number, unit: string) {
  return `${(value / scale).toFixed(2)} ${unit}`;
}

function formatScaleValue(value: number) {
  return value.toLocaleString(undefined, { maximumFractionDigits: 1 });
}

export function Waveform({
  title,
  byteRates = [],
  itemRates = [],
  emptyText,
}: {
  title: string;
  byteRates?: number[];
  itemRates?: number[];
  positions?: number[];
  emptyText: string;
}) {
  if (byteRates.length === 0 && itemRates.length === 0) return <section className="waveform"><h3>{title}</h3><p>{emptyText}</p></section>;
  return <section className="waveform" aria-label={title}>
    <h3>{title}</h3>
    <ThroughputChart title={title} byteRates={byteRates} color="copy" unit="MB/s" />
  </section>;
}
