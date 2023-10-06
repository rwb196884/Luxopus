#!/bin/sh

planDir="/home/rwb/Luxopus/plans"
f=$(ls "$planDir" | tail -1)

if [ -n "$var" ] && [ "$var" -eq "$var" ] 2>/dev/null; then
  echo "Updating plan ${planDor}/$f"
else
  echo "Parameter must be a number."
  exit 1
fi

elif [ $0 -lt 0 ]; then
	echo "Parameter 0 is new charge from grid cut-off. Must be at least 0."
	exit 1
elif [ $0 -gt 100 ]; then
	echo "Parameter 0 is new charge from grid cut-off. Must be at most 100."
	exit 1
fi

cat "${planDir}/$f" | jq ".Plans[].Action.ChargeFromGrid |= if . > 0 then $0 else . end" > "${planDir}/${f}.tmp"
mv "${planDir}/${f}.tmp" "${planDir}/$f"
