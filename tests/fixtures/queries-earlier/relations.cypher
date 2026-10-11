// How is this character related to others?
// Parameters: $character, $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (a:Character)-[r:RELATED_TO]-(b) WHERE $character IN a.aliases AND ($spoilers OR r.revealedPos <= $pos) RETURN b.name AS other, r.kind AS kind, id(b) AS otherId, id(r) AS edgeId ORDER BY r.revealedPos
