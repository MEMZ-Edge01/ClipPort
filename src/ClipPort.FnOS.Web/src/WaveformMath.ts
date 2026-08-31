export interface WaveformDisplaySample {
  sourceIndex: number;
  value: number;
}

export interface WaveformCoordinate {
  x: number;
  y: number;
}

export function getNormalizedX(sampleCount: number, index: number) {
  if (sampleCount <= 0 || index < 0 || index >= sampleCount) throw new RangeError('Invalid waveform sample index.');
  return sampleCount === 1 ? 1 : index / (sampleCount - 1);
}

export function createDisplaySamples(samples: readonly number[], maximumPointCount: number): WaveformDisplaySample[] {
  if (maximumPointCount < 4) throw new RangeError('At least four display points are required.');
  if (samples.length <= maximumPointCount) return samples.map((value, sourceIndex) => ({ sourceIndex, value }));
  const interiorCount = samples.length - 2;
  let groupSize = 1;
  while (Math.ceil(interiorCount / groupSize) * 2 > maximumPointCount - 2) groupSize *= 2;
  const result: WaveformDisplaySample[] = [{ sourceIndex: 0, value: samples[0] }];
  for (let start = 1; start < samples.length - 1; start += groupSize) {
    const end = Math.min(samples.length - 1, start + groupSize);
    let minimumIndex = start;
    let maximumIndex = start;
    for (let index = start + 1; index < end; index += 1) {
      if (samples[index] < samples[minimumIndex]) minimumIndex = index;
      if (samples[index] > samples[maximumIndex]) maximumIndex = index;
    }
    if (minimumIndex === maximumIndex) {
      result.push({ sourceIndex: minimumIndex, value: samples[minimumIndex] });
    } else {
      const first = Math.min(minimumIndex, maximumIndex);
      const second = Math.max(minimumIndex, maximumIndex);
      result.push({ sourceIndex: first, value: samples[first] });
      result.push({ sourceIndex: second, value: samples[second] });
    }
  }
  result.push({ sourceIndex: samples.length - 1, value: samples.at(-1) ?? 0 });
  return result;
}

export function createDisplayCoordinates(samples: readonly number[], maximumPointCount: number): WaveformCoordinate[] {
  return createDisplaySamples(samples, maximumPointCount).map(sample => ({
    x: getNormalizedX(samples.length, sample.sourceIndex),
    y: sample.value,
  }));
}

export function alignContinuousTransition(currentPoints: readonly WaveformCoordinate[], targetPoints: readonly WaveformCoordinate[]) {
  if (targetPoints.length === 0) return [];
  if (currentPoints.length === 0) return [...targetPoints];
  if (currentPoints.length === targetPoints.length) return [...currentPoints];
  if (targetPoints.length > currentPoints.length) {
    return targetPoints.map((_, index) => currentPoints[Math.min(index, currentPoints.length - 1)]);
  }
  return targetPoints.map((_, index) => {
    const sourceIndex = targetPoints.length === 1
      ? currentPoints.length - 1
      : Math.round(index * (currentPoints.length - 1) / (targetPoints.length - 1));
    return currentPoints[sourceIndex];
  });
}

export function easeOutCubic(progress: number) {
  const normalized = Number.isFinite(progress) ? Math.min(1, Math.max(0, progress)) : 0;
  return 1 - Math.pow(1 - normalized, 3);
}

export function getWaveformDivisionStep(displayPeak: number) {
  if (!Number.isFinite(displayPeak) || displayPeak <= 0) return 0;
  const requiredStep = displayPeak / 3;
  const magnitude = Math.pow(10, Math.floor(Math.log10(requiredStep)));
  for (const multiplier of [1, 1.5, 2, 3, 5]) {
    const candidate = multiplier * magnitude;
    if (candidate >= requiredStep) return candidate;
  }
  return 10 * magnitude;
}
