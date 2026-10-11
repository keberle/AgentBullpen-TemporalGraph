// Who do the facts talk about most?
// Characters by the number of facts about them.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (f:Fact), (c:Character) WHERE id(c) IN f.subject AND visible(f) AND visible(c)
RETURN c.name AS character, count(f) AS facts, id(c) AS characterId ORDER BY facts DESC LIMIT 10
