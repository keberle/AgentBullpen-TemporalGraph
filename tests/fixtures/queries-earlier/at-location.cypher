// Who has been placed at a location, and events that happened there.
// Parameters: $location, $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (x)-[l:LOCATED_AT]->(loc:Location) WHERE $location IN loc.aliases AND ($spoilers OR l.revealedPos <= $pos)
RETURN x.name AS who, coalesce(x.text, '') AS event, l.storyFrom AS storyTime, id(x) AS whoId, id(l) AS edgeId ORDER BY l.revealedPos
