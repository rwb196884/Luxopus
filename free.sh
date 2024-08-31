#!/bin/sh

# Params
# 1: the UTC hour 0--23 at which the free electricity starts.

planDir="/home/rwb/Luxopus/plans"
f=$(ls "$planDir" | tail -1)

if [ -z "$1" ]; then
	echo "Parameter is required."
	exit 1
fi

d1=$( date -u -d "$1" )
if [ $? -ne 0 ];  then
  echo "Parameter must be a date."
  exit 1
fi

dj=$( date -u -d "$1" +%Y-%m-%dT%H:00:00Z )
djj=$( date -u -d "$1 +1 hour" +%Y-%m-%dT%H:00:00Z )
 
echo "Setting buy price to zero at UTC $d1 in plan ${f}."
echo "dj is $dj"
echo "djj is $djj"

# Find the last entry where .Start is before d and slap it about.
p=$( cat ${planDir}/${f} | jq --arg d "$d" '[ .Plans[] | select ( .Start | fromdateiso8601 < $d ) ] | last ' )
#pNew=$( echo "$p" | jq  --arg dj "$dj" ' .Start |= $dj | .Buy |= 0 | .Action.ChargeFromGrid |= 100 | .Action.DischargeToGrid |= 100 | .New |= "NEW" ' )
pNew=$( echo "$p" | jq  --arg dj "$dj" ' .Start |= $dj | .Buy |= 0 ' )
pAfter=$( echo "$p" | jq --arg djj "$djj" ' .Start |= $djj ' )

r=$( cat ${planDir}/${f} | jq --argjson pNew "$pNew" --argjson pAfter "$pAfter" ' [ .Plans[], $pNew, $pAfter ] | sort_by( .Start | fromdateiso8601 ) | { Plans: . } ' )

echo "$r" 
exit 1

cat "${planDir}/$f" | jq ".Plans[].Action.ChargeFromGrid |= if . > 0 then $1 else . end" > "${planDir}/${f}.tmp"
mv "${planDir}/${f}.tmp" "${planDir}/$f"

