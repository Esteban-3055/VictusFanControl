"""Loopback-only test bridge to the same C++ session engine used by ESP32 firmware."""
import argparse, contextlib, pathlib, socket, subprocess, time

def main():
    p=argparse.ArgumentParser();p.add_argument('host');p.add_argument('--configuration-report');args=p.parse_args()
    with socket.socket() as listener:
        listener.bind(('127.0.0.1',0));listener.listen(1);listener.settimeout(20)
        print(listener.getsockname()[1],flush=True)
        with listener.accept()[0] as connection, contextlib.ExitStack() as stack:
            command=[str(pathlib.Path(args.host).resolve())]
            report=subprocess.DEVNULL
            if args.configuration_report:
                command.append('--configuration')
                report=stack.enter_context(open(args.configuration_report,'wb'))
            process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=report)
            try:
                connection.settimeout(3);pending=b'';deadline=time.monotonic()+20
                while time.monotonic()<deadline:
                    chunk=connection.recv(1024)
                    if not chunk:return
                    pending+=chunk
                    if len(pending)>8192:raise ValueError('fixture framing limit')
                    while b'\n' in pending:
                        line,pending=pending.split(b'\n',1)
                        process.stdin.write(line+b'\n');process.stdin.flush()
                        response=process.stdout.readline()
                        if not response or response==b'REJECT\n':raise ValueError('C++ firmware rejected Windows frame')
                        # Deliberate fragmentation also exercises the real Windows framing reader.
                        connection.sendall(response[:7]);connection.sendall(response[7:])
            finally:
                if process.stdin:process.stdin.close()
                try:process.wait(timeout=2)
                except subprocess.TimeoutExpired:process.kill();process.wait(timeout=2)
if __name__=='__main__':main()
