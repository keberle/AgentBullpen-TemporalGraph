// Who has been aboard the Hispaniola?
// Characters and events placed aboard the Hispaniola.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (x)-[l:LOCATED_AT]->(loc:Location) WHERE 'Hispaniola' IN loc.aliases AND visible(l) AND visible(x) AND visible(loc)
RETURN coalesce(x.name, '') AS who, coalesce(x.summary, '') AS event, l.revealedPos AS revealed, id(x) AS whoId, id(l) AS edgeId
ORDER BY l.revealedPos
