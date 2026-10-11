// The 3D tab's WebGL context. A browser keeps about 16 live contexts per process (same-site tabs share one) and, past that,
// drops the oldest: its canvas goes blank (white, on our dark page). three.js's renderer.dispose() frees the renderer's GPU objects
// but not the context itself, which otherwise lives until the canvas is garbage-collected, so every visit to the 3D tab used to
// leave one behind. Two helpers: give a context back when its graph is unmounted, and hear about a context that is lost anyway.

/** The part of three's WebGLRenderer used here. */
export type ReleasableRenderer = {
  dispose(): void;
  forceContextLoss(): void;
  getContext(): { isContextLost(): boolean };
  domElement: { isConnected: boolean };
};

/**
 * Gives the renderer's WebGL context back to the browser, unless its canvas is still in the page (React's StrictMode runs an
 * effect's cleanup once on mount without unmounting anything: that graph is still in use). dispose() first: it removes three's own
 * context-lost listener, so the forced loss logs nothing. A context that is already lost has nothing left to give up (and three
 * would warn that it can't). Returns whether the renderer was released.
 */
export function releaseContext(renderer: ReleasableRenderer | null | undefined): boolean {
  if (!renderer || renderer.domElement.isConnected) return false;
  renderer.dispose();
  if (!renderer.getContext().isContextLost()) renderer.forceContextLoss();
  return true;
}

/** Calls back when the canvas loses its WebGL context and when the browser gives it back; returns the function that stops listening. */
export function watchContext(canvas: Pick<EventTarget, "addEventListener" | "removeEventListener">, onLost: () => void, onRestored: () => void): () => void {
  canvas.addEventListener("webglcontextlost", onLost);
  canvas.addEventListener("webglcontextrestored", onRestored);
  return () => {
    canvas.removeEventListener("webglcontextlost", onLost);
    canvas.removeEventListener("webglcontextrestored", onRestored);
  };
}
