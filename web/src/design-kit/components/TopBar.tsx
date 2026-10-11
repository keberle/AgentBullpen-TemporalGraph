import type { ReactNode } from "react";

// App top bar — TopBar.dc.html / Authenticated.dc.html: 64px, 0 24px padding,
// divider hairline. `right` takes the user menu when signed in; a standalone
// demo simply passes nothing (and never imports UserMenu).
export function TopBar({ left, right, sticky = false }: { left: ReactNode; right?: ReactNode; sticky?: boolean }) {
  return (
    <header
      className={`flex h-64 items-center justify-between gap-16 border-b border-divider bg-page px-24 ${sticky ? "sticky top-0 z-10" : ""}`}
    >
      <div className="flex flex-wrap items-center gap-32">{left}</div>
      {right}
    </header>
  );
}
