// What has the Cardinal done?
// Events whose summary, or a participant or place, contains 'cardinal'.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (e:Event)-[r:PARTICIPATED_IN|LOCATED_AT]-(x) WHERE visible(e) AND visible(r) AND visible(x)
  AND (toLower(e.summary) CONTAINS 'cardinal' OR toLower(x.name) CONTAINS 'cardinal')
WITH e, min(r.revealedPos) AS revealed
OPTIONAL MATCH (e)-[a:PARTICIPATED_IN|LOCATED_AT]-(o) WHERE visible(a) AND visible(o)
WITH e, revealed, collect(o.name) AS who, collect(id(o)) AS whoIds, collect(id(a)) AS edgeIds
RETURN e.summary AS event, who, revealed, id(e) AS eventId, whoIds, edgeIds ORDER BY revealed
