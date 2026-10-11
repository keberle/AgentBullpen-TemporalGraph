// Characters with no located-at edge yet (exercises OPTIONAL MATCH).
// Parameters: $pos, $spoilers.
MATCH (c:Character) WHERE visible(c) OPTIONAL MATCH (c)-[l:LOCATED_AT]->(loc:Location) WHERE visible(l) WITH c, count(loc) AS places WHERE places = 0 RETURN c.name AS name
