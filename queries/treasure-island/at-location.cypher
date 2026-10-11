// Who has been placed at a location, and events that happened there.
// Parameters: $location, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (x)-[l:LOCATED_AT]->(loc:Location) WHERE $location IN loc.aliases AND visible(l) AND visible(x) AND visible(loc)
RETURN x.name AS who, coalesce(x.summary, '') AS event, l.storyFrom AS storyTime, id(x) AS whoId, id(l) AS edgeId ORDER BY l.revealedPos
