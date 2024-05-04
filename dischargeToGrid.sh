#!/bin/sh

planDir="/home/rwb/Luxopus/plans"
f=$(ls "$planDir" | tail -1)

if [ -z "$1" ]; then
	echo "Parameter is required."
	exit 1
fi

if [ "$1" -eq "$1" ] 2>/dev/null;  then
	# ok
	z="z"
else
  echo "Parameter must be a number."
  exit 1
fi

if [ $1 -lt 0 ]; then
	echo "Parameter 0 is new charge from grid cut-off. Must be at least 0."
	exit 1
elif [ $1 -gt 100 ]; then
	echo "Parameter 0 is new charge from grid cut-off. Must be at most 100."
	exit 1
fi
 
echo "Setting DiscahrgeToGrid to $1 where value is > 0 in ${planDir}/$f"

cat "${planDir}/$f" | jq ".Plans[].Action.DischargeToGrid |= if . < 100 then $1 else . end" > "${planDir}/${f}.tmp"
mv "${planDir}/${f}.tmp" "${planDir}/$f"

