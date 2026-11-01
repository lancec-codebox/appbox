"""Packs a zip into a CRX3 file signed with an RSA key (PEM), using only hashlib and the openssl CLI.

Usage: python pack_crx.py <key.pem> <extension.zip> <out.crx>
Prints the extension id.
"""
import hashlib
import struct
import subprocess
import sys


def varint(n):
    out = bytearray()
    while True:
        byte = n & 0x7F
        n >>= 7
        if n:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def field(number, data):
    """A length-delimited protobuf field."""
    return varint(number << 3 | 2) + varint(len(data)) + data


def main(key_path, zip_path, crx_path):
    public_key = subprocess.run(['openssl', 'rsa', '-in', key_path, '-pubout', '-outform', 'DER'],
                                check=True, capture_output=True).stdout
    crx_id = hashlib.sha256(public_key).digest()[:16]
    signed_data = field(1, crx_id)  # SignedData.crx_id
    with open(zip_path, 'rb') as f:
        archive = f.read()
    to_sign = b'CRX3 SignedData\x00' + struct.pack('<I', len(signed_data)) + signed_data + archive
    signature = subprocess.run(['openssl', 'dgst', '-sha256', '-sign', key_path],
                               input=to_sign, check=True, capture_output=True).stdout
    proof = field(1, public_key) + field(2, signature)  # AsymmetricKeyProof
    header = field(2, proof) + field(10000, signed_data)  # CrxFileHeader: sha256_with_rsa, signed_header_data
    with open(crx_path, 'wb') as f:
        f.write(b'Cr24' + struct.pack('<I', 3) + struct.pack('<I', len(header)) + header + archive)
    print(''.join(chr(ord('a') + int(c, 16)) for c in crx_id.hex()))


if __name__ == '__main__':
    main(*sys.argv[1:4])
