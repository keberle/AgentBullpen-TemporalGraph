// "Pick a book" card button — copied from the prototype's ProtoHeader.tsx. With a hero, the 3:2 image sits above the
// title and author inside the card's own border and radius (the frame is HTML; the image is never cropped by hand, only by
// object-fit). Without one, the card is unchanged.
export function BookCard({ title, author, hero, onClick }: { title: string; author: string; hero?: string | null; onClick: () => void }) {
  if (!hero)
    return (
      <button
        type="button"
        onClick={onClick}
        className="box-border flex min-h-220 cursor-pointer flex-col justify-end gap-6 rounded-16 border border-divider bg-panel p-28 text-left font-sans text-fg"
      >
        <span className="text-22 leading-[1.2] font-bold">{title}</span>
        <span className="text-15 text-fg-muted">{author}</span>
      </button>
    );
  return (
    <button
      type="button"
      onClick={onClick}
      className="box-border flex cursor-pointer flex-col overflow-hidden rounded-16 border border-divider bg-panel p-0 text-left font-sans text-fg"
    >
      <img src={`/${hero}`} alt={title} loading="lazy" className="block aspect-[3/2] w-full object-cover" />
      <span className="flex flex-col gap-6 p-28">
        <span className="text-22 leading-[1.2] font-bold">{title}</span>
        <span className="text-15 text-fg-muted">{author}</span>
      </span>
    </button>
  );
}
