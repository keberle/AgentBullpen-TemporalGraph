import { useEffect, useRef, useState } from "react";

export type Size = { width: number; height: number };

/**
 * The size to keep after a measurement. A measurement with no width or no height (the element hidden, or squeezed out for a
 * moment by a layout change) keeps the last real size, so a graph mounted at that size is never unmounted for it; an unchanged size
 * keeps the same object, so nothing re-renders.
 */
export function heldSize(previous: Size, measured: Size): Size {
  const width = Math.floor(measured.width);
  const height = Math.floor(measured.height);
  if (width <= 0 || height <= 0) return previous;
  return width === previous.width && height === previous.height ? previous : { width, height };
}

/**
 * A ref and the element's size, kept current with a ResizeObserver (the graph canvases need explicit pixel sizes). 0 × 0 until the
 * first real measurement, then always the last real one (heldSize).
 */
export function useElementSize<T extends HTMLElement>() {
  const ref = useRef<T>(null);
  const [size, setSize] = useState<Size>({ width: 0, height: 0 });
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver(([e]) => setSize((previous) => heldSize(previous, e.contentRect)));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, size] as const;
}
