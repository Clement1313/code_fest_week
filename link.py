import socket
from pynput.keyboard import Controller, Key

HOST = "127.0.0.1"
PORT = 5000

KEYS = {
    "haut": Key.up,
    "bas": Key.down,
    "gauche": Key.left,
    "droite": Key.right,
    "up": Key.up,
    "down": Key.down,
    "left": Key.left,
    "right": Key.right,
    "space": Key.space,
    "enter": Key.enter,
    "esc": Key.esc,
}

keyboard = Controller()

with socket.create_server((HOST, PORT)) as server:
    print(f"Listening on {HOST}:{PORT}", flush=True)

    connection, address = server.accept()

    with connection:
        buffer = b""

        while True:
            data = connection.recv(4096)

            if not data:
                break

            buffer += data

            while b"\n" in buffer:
                line, buffer = buffer.split(b"\n", 1)
                command = line.strip().lower().decode("ascii", errors="ignore")

                key = KEYS.get(command)

                if key is not None:
                    keyboard.press(key)
                    keyboard.release(key)
