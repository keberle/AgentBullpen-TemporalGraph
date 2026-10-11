// Who do the facts so far talk about most?
// Parameters: $pos, $spoilers.
// Columns ending in Id/Ids carry the node ids of the name columns, for the chat tools (which strip them).
MATCH (f:Fact)-[r:ABOUT]->(c:Character) WHERE ($spoilers OR r.revealedPos <= $pos) RETURN c.name AS character, count(f) AS facts, id(c) AS characterId ORDER BY facts DESC LIMIT 10
