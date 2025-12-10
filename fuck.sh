#!/bin/sh

planDir="/home/rwb/Luxopus/plans"
f=$( ls --hide *.fuck ${planDir}/202*__* 2>/dev/null | tail -1 )

if [ ! -f "$f" ]; then
	echo "No file '$f' at '${planDir}/202*__*'."
	exit 1
fi

cat "$f" | jq '.Plans | map (if .Buy > 30 then . | .Action.DischargeToGrid |= 100 | .Action.ChargeFromGrid |= 0 else . | .Action.DischargeToGrid |= 100 | .Action.ChargeFromGrid |= 100 end) | { "Plans": . }' >"${f}.fuck"
mv "${f}.fuck" "$f"

