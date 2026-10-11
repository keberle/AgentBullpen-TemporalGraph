// Events whose summary, or the name of a participant or place, contains a term (events reached by PARTICIPATED_IN / LOCATED_AT).
// Parameters: $term, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (e:Event)-[r:PARTICIPATED_IN|LOCATED_AT]-(x) WHERE visible(e) AND visible(r) AND visible(x)
  AND (toLower(e.summary) CONTAINS toLower($term) OR toLower(x.name) CONTAINS toLower($term))
WITH e, min(r.revealedPos) AS revealed
OPTIONAL MATCH (e)-[a:PARTICIPATED_IN|LOCATED_AT]-(o) WHERE visible(a) AND visible(o)
WITH e, revealed, collect(o.name) AS about, collect(id(o)) AS aboutIds, collect(id(a)) AS edgeIds
RETURN e.summary AS fact, about, revealed, id(e) AS factId, aboutIds, edgeIds ORDER BY revealed
