import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import {
  ThroughputChart,
} from './Waveform';
import {
  alignContinuousTransition,
  createDisplayCoordinates,
  createDisplaySamples,
  easeOutCubic,
  getWaveformDivisionStep,
} from './WaveformMath';

describe('Windows-compatible waveform calculations', () => {
  it('uses the same four-level pleasant division steps as Windows', () => {
    expect([0, 1.14, 4.1, 8, 10, 35, 60, 99, 100, 350, 401, 600, 601]
      .map(getWaveformDivisionStep)).toEqual([0, 0.5, 1.5, 3, 5, 15, 20, 50, 50, 150, 150, 200, 300]);
  });

  it('compresses long histories while retaining both edges and bucket extrema', () => {
    const samples = Array.from({ length: 1026 }, (_, index) => index % 8 === 0 ? 1000 : index % 7);
    const displayed = createDisplaySamples(samples, 128);
    expect(displayed.length).toBeLessThanOrEqual(130);
    expect(displayed[0]).toEqual({ sourceIndex: 0, value: samples[0] });
    expect(displayed.at(-1)).toEqual({ sourceIndex: samples.length - 1, value: samples.at(-1) });
    expect(displayed.some(sample => sample.value === 1000)).toBe(true);
  });

  it('maps samples across the whole timeline and keeps appended points continuous', () => {
    expect(createDisplayCoordinates([10, 20, 30], 64)).toEqual([
      { x: 0, y: 10 }, { x: 0.5, y: 20 }, { x: 1, y: 30 },
    ]);
    expect(alignContinuousTransition([{ x: 0, y: 10 }, { x: 1, y: 20 }], [
      { x: 0, y: 10 }, { x: 0.5, y: 20 }, { x: 1, y: 30 },
    ])).toEqual([{ x: 0, y: 10 }, { x: 1, y: 20 }, { x: 1, y: 20 }]);
  });

  it('matches the 180 ms animation easing endpoints', () => {
    expect(easeOutCubic(0)).toBe(0);
    expect(easeOutCubic(0.5)).toBeCloseTo(0.875);
    expect(easeOutCubic(1)).toBe(1);
    expect(easeOutCubic(Number.NaN)).toBe(0);
  });
});

describe('ThroughputChart', () => {
  it('renders an empty four-level SVG chart without Canvas', () => {
    const { container } = render(<ThroughputChart title="文件拷贝大小速度" color="copy" unit="MB/s" />);
    expect(screen.getByText('0.00 MB/s')).toBeInTheDocument();
    expect(container.querySelectorAll('.throughput-grid-line')).toHaveLength(4);
    expect(container.querySelector('canvas')).not.toBeInTheDocument();
  });

  it('renders a single reading across the full visible interval', () => {
    const { container } = render(<ThroughputChart title="项目数速度" byteRates={[6.3]} color="verify" unit="个/s" />);
    expect(screen.getByText('6.30 个/s')).toBeInTheDocument();
    const points = container.querySelector('.throughput-line')?.getAttribute('points') ?? '';
    expect(points.startsWith('0.00,')).toBe(true);
    expect(points).toContain('1000.00,');
  });
});
