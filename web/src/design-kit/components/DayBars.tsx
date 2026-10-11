// Per-day bar chart, inline SVG exactly as Usage.dc.html draws it (viewBox
// 520×200, plot x 40–520, baseline y 170, 150px tall, accent bars with rx 2,
// two faint grid lines, a solid baseline, max/mid/0 labels, start / "Today").
// No chart library: that choice is still open.

import { fmtTokens } from "../format.ts";

export type DayBar = { value: number; tip: string };

const label = { fill: "rgba(255,255,255,0.6)", fontSize: 11, fontFamily: "var(--abp-font-sans)" } as const;

export function DayBars({ title, bars, max, startLabel }: { title: string; bars: readonly DayBar[]; max: number; startLabel: string }) {
  const n = bars.length;
  const step = 480 / n;
  const w = Math.max(2, step - 2);
  return (
    <svg viewBox="0 0 520 200" className="block h-auto w-full" role="img" aria-label={`${title} per day`}>
      <line x1="40" y1="20" x2="520" y2="20" stroke="var(--abp-track)" strokeWidth="1" />
      <line x1="40" y1="95" x2="520" y2="95" stroke="var(--abp-track)" strokeWidth="1" />
      <line x1="40" y1="170" x2="520" y2="170" stroke="var(--abp-outline)" strokeWidth="1" />
      <text x="32" y="24" textAnchor="end" {...label}>
        {fmtTokens(max)}
      </text>
      <text x="32" y="99" textAnchor="end" {...label}>
        {fmtTokens(max / 2)}
      </text>
      <text x="32" y="174" textAnchor="end" {...label}>
        0
      </text>
      {bars.map((b, i) => {
        const h = Math.round((Math.min(b.value, max) / max) * 150);
        return (
          <rect key={i} x={(40 + i * step + 1).toFixed(1)} y={170 - h} width={w.toFixed(1)} height={h} rx="2" fill="var(--abp-accent)">
            <title>{b.tip}</title>
          </rect>
        );
      })}
      <text x="40" y="192" {...label}>
        {startLabel}
      </text>
      <text x="520" y="192" textAnchor="end" {...label}>
        Today
      </text>
    </svg>
  );
}
