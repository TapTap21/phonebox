#!/usr/bin/env python3
"""Publish read-only DeyeCloud telemetry to the local PhoneBox agent."""
import argparse
from email.utils import parsedate_to_datetime
import hashlib
import json
import math
import os
from pathlib import Path
import time
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

DEYE = "https://eu1-developer.deyecloud.com/v1.0"
PHONEBOX = "http://127.0.0.1:8765"
INTERVAL = 120
MAX_AGE = 900
MAX_BACKOFF = 3600
TILES = {
    "battery": "Battery",
    "grid": "Grid connection",
    "load": "Home load",
    "solar": "Solar generation",
    "voltage": "Grid voltage",
    "status": "Inverter",
}


def post(url, data, authorization=None):
    headers = {"Content-Type": "application/json"}
    if authorization:
        headers["Authorization"] = authorization
    request = Request(url, json.dumps(data).encode(), headers, method="POST")
    with urlopen(request, timeout=15) as response:
        return json.load(response)


class DeyeRetry(RuntimeError):
    def __init__(self, error):
        super().__init__(f"DeyeCloud returned HTTP {error.code}")
        self.retry_after = 0
        header = error.headers.get("Retry-After", "").strip()
        if header.isascii() and header.isdecimal():
            self.retry_after = int(header)
        elif header:
            try:
                self.retry_after = max(0, parsedate_to_datetime(header).timestamp() - time.time())
            except (ValueError, OverflowError):
                pass


class Deye:
    def __init__(self, credentials):
        self.credentials = credentials
        self.token = None

    def request(self, path, body, token=None):
        try:
            response = post(DEYE + path, body, token)
        except HTTPError as error:
            if error.code == 429 or 500 <= error.code < 600:
                raise DeyeRetry(error) from None
            raise
        if response.get("success") is not True:
            # The response message is intentionally omitted from logs.
            raise RuntimeError(f"DeyeCloud rejected {path.split('?')[0]} (code {response.get('code')})")
        return response

    def read(self, path, body):
        if self.token is None or time.monotonic() >= self.expires:
            c = self.credentials
            response = self.request("/account/token?" + urlencode({"appId": c["app-id"]}), {
                "appSecret": c["app-secret"],
                "email": c["email"],
                "password": hashlib.sha256(c["password"].encode()).hexdigest(),
            })
            token = response["accessToken"]
            self.token = token if token.lower().startswith("bearer ") else f"Bearer {token}"
            self.expires = time.monotonic() + max(0, int(response.get("expiresIn", 3600)) - 60)
        try:
            return self.request(path, body, self.token)
        except HTTPError as error:
            if error.code == 401:
                self.token = None
            raise

    def discover(self):
        stations = self.read("/station/list", {"page": 1, "size": 20})["stationList"]
        if len(stations) != 1:
            raise RuntimeError("Select a DeyeCloud station with --station")
        return int(stations[0]["id"])

    def inverter(self, station):
        result = self.read("/station/device", {"stationIds": [station], "page": 1, "size": 20})
        serials = [row["deviceSn"] for row in result["deviceListItems"] if row["deviceType"] == "INVERTER"]
        if len(serials) != 1:
            raise RuntimeError("Select a DeyeCloud inverter with --serial")
        return serials[0]


def number(value):
    try:
        parsed = float(value)
        return parsed if math.isfinite(parsed) else None
    except (ValueError, TypeError):
        return None


def reading(value, unit="", sub=None, level=None):
    if value is None:
        return {"value": "Unavailable", "level": "warn", "sub": sub}
    return {"value": value, "unit": unit, "sub": sub, "level": level}


def tiles(device, now):
    age = now - (number(device.get("collectionTime")) or 0)
    state = {1: "Online", 2: "Alert", 3: "Offline"}.get(device.get("deviceState"), "Unknown")
    if age > MAX_AGE or age < -60 or state == "Offline":
        return missing("Inverter offline" if state == "Offline" else "DeyeCloud data is stale")

    points = {row["key"]: row.get("value") for row in device.get("dataList", []) if "key" in row}

    def measure(key, unit):
        value = number(points.get(key))
        return f"{value:g} {unit}" if value is not None else None

    voltage = number(points.get("GridVoltageL1L2"))
    battery_voltage = measure("BatteryVoltage", "V")
    battery_current = measure("BatteryCurrent", "A")
    battery_detail = " · ".join(filter(None, (battery_voltage, battery_current)))
    grid_power = measure("TotalGridPower", "W net")
    relay = str(points.get("GridRelayStatus") or "").strip()
    if relay.casefold() == "pull-in":
        grid = reading("Connected", sub=grid_power, level="ok")
    elif relay.casefold() in {"release", "released"}:
        grid = reading("Disconnected", sub=grid_power, level="warn")
    else:
        grid = reading(None, sub=f"Relay: {relay}" if relay else "No grid relay reading")

    return {
        "battery": reading(number(points.get("SOC")), "%", battery_detail),
        "grid": grid,
        "load": reading(number(points.get("TotalConsumptionPower")), "W",
                        measure("DailyConsumption", "kWh today")),
        "solar": reading(number(points.get("TotalDCInputPower")), "W",
                         measure("PVDailyPowerGenerationActive", "kWh today")),
        "voltage": reading(voltage, "V", measure("GridFrequency", "Hz")),
        "status": reading(state, sub=f"DeyeCloud sample {time.strftime('%H:%M', time.localtime(now - age))}",
                          level="ok" if state == "Online" else "warn"),
    }


def missing(reason):
    return {key: reading(None, sub=reason) for key in TILES}


def publish(snapshot):
    for key, value in snapshot.items():
        post(f"{PHONEBOX}/api/push/deye-{key}?ttl={3 * INTERVAL}", value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--station", type=int)
    parser.add_argument("--serial")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    directory = Path(os.environ["CREDENTIALS_DIRECTORY"])
    deye = Deye({k: (directory / k).read_text().rstrip("\r\n") for k in ("app-id", "app-secret", "email", "password")})
    station, serial = args.station, args.serial
    backoff = INTERVAL
    while True:
        delay = INTERVAL
        try:
            if station is None:
                station = deye.discover()
            if serial is None:
                serial = deye.inverter(station)
            latest = deye.read("/device/latest", {"deviceList": [serial]})
            device = next(row for row in latest["deviceDataList"] if row["deviceSn"] == serial)
            backoff = INTERVAL
            publish(tiles(device, time.time()))
        except (OSError, RuntimeError, ValueError, KeyError, StopIteration) as error:
            if isinstance(error, DeyeRetry):
                backoff = min(MAX_BACKOFF, 2 * backoff)
                delay = max(backoff, error.retry_after)
                print(f"{error}; retrying in {math.ceil(delay)} seconds", flush=True)
            elif isinstance(error, (HTTPError, RuntimeError)):
                print(error, flush=True)
            else:
                print(f"DeyeCloud update failed ({type(error).__name__})", flush=True)
            if args.once:
                raise SystemExit(1) from None
            try:
                publish(missing("Could not refresh DeyeCloud"))
            except (OSError, ValueError):
                pass
        if args.once:
            return
        time.sleep(delay)


if __name__ == "__main__":
    main()
