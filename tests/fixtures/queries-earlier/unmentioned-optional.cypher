// Characters with no located-at edge yet (exercises OPTIONAL MATCH).
// Parameters: $pos.
MATCH (c:Character) WHERE c.evidenceStart < $pos OPTIONAL MATCH (c)-[l:LOCATED_AT]->(loc:Location) WITH c, count(loc) AS places WHERE places = 0 RETURN c.name AS name
