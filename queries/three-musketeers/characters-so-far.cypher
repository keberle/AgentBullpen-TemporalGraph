// Which characters has the reader met by this point (Spoilers on: which characters the story has reached)?
// Parameters: $pos (a text position; the chapter slider and query --chapter N set it to the end of chapter N), $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry the node ids of the name columns, for the chat tools (which strip them).
MATCH (c:Character) WHERE visible(c) RETURN c.name AS name, id(c) AS nameId ORDER BY c.revealedPos
