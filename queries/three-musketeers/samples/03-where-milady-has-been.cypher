// Where has Milady been placed?
// LOCATED_AT edges of Milady de Winter (alias 'Milady').
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (c:Character)-[l:LOCATED_AT]->(loc:Location) WHERE 'Milady' IN c.aliases AND visible(l) AND visible(c) AND visible(loc)
RETURN loc.name AS location, l.revealedPos AS revealed, id(loc) AS locationId, id(l) AS edgeId ORDER BY l.revealedPos
