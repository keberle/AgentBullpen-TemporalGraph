// Types for forceLayout.js.
export const ALGORITHM: string;
export const SEED: number;
export const ITERATIONS: number;
export function nodeRadius(label: string, degree: number): number;
export type LayoutPosition = { x: number; y: number; z: number; x2: number; y2: number };
export type LayoutSummary = { algorithm: string; seed: number; iterations: number; nodes: number; edges: number; alpha3: number; alpha2: number; radius3: number; radius2: number };
export function layoutGraph(
  nodes: readonly { id: number; label: string }[],
  edges: readonly { source: number; target: number }[],
  options?: { seed?: number; iterations?: number },
): { positions: Map<number, LayoutPosition>; summary: LayoutSummary };
