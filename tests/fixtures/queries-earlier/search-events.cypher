// Events whose text, or the name of a participant or place, contains a term (events reached by PARTICIPATED_IN / LOCATED_AT).
// Parameters: $term, $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (e:Event)-[r:PARTICIPATED_IN|LOCATED_AT]-(x) WHERE ($spoilers OR r.revealedPos <= $pos)
  AND (toLower(e.text) CONTAINS toLower($term) OR toLower(x.name) CONTAINS toLower($term))
WITH e, min(r.revealedPos) AS revealed
OPTIONAL MATCH (e)-[a:PARTICIPATED_IN|LOCATED_AT]-(o) WITH e, revealed, collect(o.name) AS about, collect(id(o)) AS aboutIds, collect(id(a)) AS edgeIds
RETURN e.text AS fact, about, revealed, id(e) AS factId, aboutIds, edgeIds ORDER BY revealed
