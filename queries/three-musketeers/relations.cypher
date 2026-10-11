// How is this character related to others?
// Parameters: $character, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (a:Character)-[r:RELATED_TO]-(b) WHERE $character IN a.aliases AND visible(a) AND visible(r) AND visible(b)
RETURN b.name AS other, r.kind AS kind, id(b) AS otherId, id(r) AS edgeId ORDER BY r.revealedPos
