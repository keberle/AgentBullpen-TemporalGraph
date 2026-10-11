import { describe, expect, it } from "vitest";
import { heldSize } from "./useElementSize.ts";
import { releaseContext, watchContext } from "./webgl.ts";

// The pieces that keep the 3D tab to one WebGL context: the held size, the release on unmount, the lost-context events.

describe("The pane's size is held", () => {
  const at = { width: 1502, height: 865 };

  it("a measurement with no width or no height keeps the last real size (the same object: nothing re-renders)", () => {
    expect(heldSize(at, { width: 0, height: 0 })).toBe(at);
    expect(heldSize(at, { width: 0, height: 865 })).toBe(at);
    expect(heldSize(at, { width: 1502, height: 0 })).toBe(at);
    expect(heldSize(at, { width: 0.4, height: 300 })).toBe(at);
  });

  it("an unchanged size keeps the same object; a new real size replaces it, floored", () => {
    expect(heldSize(at, { width: 1502.6, height: 865.2 })).toBe(at);
    expect(heldSize(at, { width: 1142.7, height: 865 })).toEqual({ width: 1142, height: 865 });
  });

  it("before the first real measurement the size stays 0 × 0", () => {
    const none = { width: 0, height: 0 };
    expect(heldSize(none, { width: 0, height: 0 })).toBe(none);
    expect(heldSize(none, { width: 800, height: 600 })).toEqual({ width: 800, height: 600 });
  });
});

function renderer(isConnected: boolean, lost = false) {
  const calls: string[] = [];
  return {
    calls,
    domElement: { isConnected },
    dispose: () => void calls.push("dispose"),
    forceContextLoss: () => void calls.push("forceContextLoss"),
    getContext: () => ({ isContextLost: () => lost }),
  };
}

describe("A renderer's WebGL context goes back to the browser when its graph is unmounted", () => {
  it("canvas out of the page: disposed, then the context given up (in that order, so three logs nothing)", () => {
    const r = renderer(false);
    expect(releaseContext(r)).toBe(true);
    expect(r.calls).toEqual(["dispose", "forceContextLoss"]);
  });

  it("canvas still in the page (StrictMode's cleanup on mount): left alone", () => {
    const r = renderer(true);
    expect(releaseContext(r)).toBe(false);
    expect(r.calls).toEqual([]);
  });

  it("a context that is already lost: disposed only (there is nothing left to give up)", () => {
    const r = renderer(false, true);
    expect(releaseContext(r)).toBe(true);
    expect(r.calls).toEqual(["dispose"]);
  });

  it("no renderer: nothing to do", () => {
    expect(releaseContext(undefined)).toBe(false);
    expect(releaseContext(null)).toBe(false);
  });
});

describe("A lost context is heard", () => {
  it("lost and restored call back; after stop, neither does", () => {
    const canvas = new EventTarget();
    const heard: string[] = [];
    const stop = watchContext(canvas, () => heard.push("lost"), () => heard.push("restored"));
    canvas.dispatchEvent(new Event("webglcontextlost"));
    canvas.dispatchEvent(new Event("webglcontextrestored"));
    expect(heard).toEqual(["lost", "restored"]);
    stop();
    canvas.dispatchEvent(new Event("webglcontextlost"));
    canvas.dispatchEvent(new Event("webglcontextrestored"));
    expect(heard).toEqual(["lost", "restored"]);
  });
});
