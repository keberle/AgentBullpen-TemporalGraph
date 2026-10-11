// Where have characters been placed so far?
// Parameters: $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (c:Character)-[l:LOCATED_AT]->(loc:Location) WHERE ($spoilers OR l.revealedPos <= $pos) RETURN c.name AS character, loc.name AS location, l.storyFrom AS storyTime, id(c) AS characterId, id(loc) AS locationId, id(l) AS edgeId ORDER BY l.revealedPos
