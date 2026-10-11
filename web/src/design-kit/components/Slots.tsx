import type { ReactNode } from "react";

// Visible placeholders for images that don't exist yet.

/** Robot-art slot: dashed white-20% border (Main tiles: 1/1 radius 8; SignIn A8: 4/3 radius 12). */
export function ArtSlot({ children, shape = "tile" }: { children: ReactNode; shape?: "tile" | "wide" }) {
  return (
    <div
      className={`flex flex-col items-center justify-center gap-4 border-2 border-dashed border-outline text-14 text-fg-muted ${
        shape === "tile" ? "aspect-square rounded-8" : "aspect-[4/3] rounded-12"
      }`}
    >
      {children}
    </div>
  );
}

/** Image slot: panel fill, radius 16, label bottom-left in 13px faint text (Public / Challenge). */
export function ImageSlot({ label, aspect, className = "" }: { label: string; aspect: "3/2" | "16/10"; className?: string }) {
  return (
    <div
      className={`box-border flex items-end rounded-16 bg-panel p-16 text-13 text-fg-faint ${aspect === "3/2" ? "aspect-[3/2]" : "aspect-[16/10]"} ${className}`}
    >
      {label}
    </div>
  );
}
