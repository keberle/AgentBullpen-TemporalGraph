import type { ReactNode } from "react";

// "AgentBullpen.AI" text wordmark (logo A10 is still open). 700 18px, ".AI" in
// the accent, optional suffix in 400 secondary — Main / TopBar mockups.
export function Wordmark({ suffix }: { suffix?: ReactNode }) {
  return (
    <span className="text-18 font-bold text-fg">
      AgentBullpen<span className="text-accent">.AI</span>
      {suffix && <span className="font-normal text-fg-muted"> {suffix}</span>}
    </span>
  );
}
