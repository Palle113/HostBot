''HostBot - Warcraft 3 game hosting bot
''Copyright (C) 2008 Craig Gidney
''
''This program is free software: you can redistribute it and/or modify
''it under the terms of the GNU General Public License as published by
''the Free Software Foundation, either version 3 of the License, or
''(at your option) any later version.
''
''This program is distributed in the hope that it will be useful,
''but WITHOUT ANY WARRANTY; without even the implied warranty of
''MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
''GNU General Public License for more details.
''You should have received a copy of the GNU General Public License
''along with this program.  If not, see http://www.gnu.org/licenses/

''''Implements the various cryptographic checks and algorithms blizzard uses
Namespace Bnet.Crypt
    Public Module common
#Region "Constants"
        Private ReadOnly G As BigNum = 47
        Private ReadOnly N As BigNum = BigNum.fromString("112624315653284427036559548610503669920632123929604336254260115573677366691719", 10)
#End Region

#Region "crc32"
        '''<summary>Computes the crc32 value for a stream of data.</summary>
        '''<param name="s">The stream to read data from.</param>
        '''<param name="length">The total amount of data to read. -1 means read entire stream.</param>
        '''<param name="poly">The polynomial to be used, specified in a bit pattern. (Default is CRC-32-IEEE 802.3).</param>
        '''<param name="polyAlreadyReversed">Indicates whether or not the bit pattern of the polynomial is already reversed.</param>
        '''<returns>crc32 value</returns>
        Public Function crc32(ByVal s As IO.Stream, Optional ByVal length As Long = -1, Optional ByVal poly As UInteger = &H4C11DB7, Optional ByVal polyAlreadyReversed As Boolean = False) As UInteger
            Dim r = New IO.BinaryReader(s)
            Dim reg As UInteger

            'Reverse the polynomial
            If polyAlreadyReversed = False Then
                Dim polyRev As UInteger = 0
                For i = 0 To 31
                    If ((poly >> i) And &H1) <> 0 Then
                        polyRev = polyRev Or (CUInt(&H1) << (31 - i))
                    End If
                Next i
                poly = polyRev
            End If

            'Precompute the combined XOR masks for each byte
            Dim xorTable(0 To 255) As UInteger
            For i = 0 To 255
                reg = CUInt(i)
                For j = 0 To 7
                    If (reg And CUInt(&H1)) <> 0 Then
                        reg = (reg >> 1) Xor poly
                    Else
                        reg >>= 1
                    End If
                Next j
                xorTable(i) = reg
            Next i

            'Direct Table Algorithm
            reg = Not CUInt(0)
            If length = -1 Then length = s.Length - s.Position
            For i As Long = 0 To length - 1
                reg = (reg >> 8) Xor xorTable(r.ReadByte() Xor CByte(reg And &HFF))
            Next i

            Return Not reg
        End Function
#End Region

#Region "Generate Keys"
        Public Function generatePublicPrivateKeyPair(Optional ByVal r As System.Random = Nothing) As Pair(Of Byte(), Byte())
            If r Is Nothing Then r = New System.Random()

            Dim privateKey = N.randomUniformUpTo(r, False, False)
            Dim privateKeyBytes = privateKey.tobytes().toarray()

            Dim publicKey = Bnet.Crypt.G.powerMod(privateKey, N)
            Dim publicKeyBytes = publicKey.toBytes().ToArray()

            ReDim Preserve privateKeyBytes(0 To 31)
            ReDim Preserve publicKeyBytes(0 To 31)

            Return New Pair(Of Byte(), Byte())(publicKeyBytes, privateKeyBytes)
        End Function
#End Region

#Region "Password Proofs"
        Public Function generateClientServerPasswordProofs( _
                    ByVal username As String, _
                    ByVal password As String, _
                    ByVal account_salt As Byte(), _
                    ByVal server_offset_public_key_bytes As Byte(), _
                    ByVal client_private_key_bytes As Byte(), _
                    ByVal client_public_key_bytes As Byte() _
                ) As Pair(Of Byte(), Byte())
            If Not (username IsNot Nothing) Then Throw New ArgumentNullException()
            If Not (password IsNot Nothing) Then Throw New ArgumentNullException()
            If Not (client_private_key_bytes IsNot Nothing) Then Throw New ArgumentNullException()
            If Not (client_public_key_bytes IsNot Nothing) Then Throw New ArgumentNullException()
            If Not (server_offset_public_key_bytes IsNot Nothing) Then Throw New ArgumentNullException()
            If Not (account_salt IsNot Nothing) Then Throw New ArgumentNullException()

            username = username.ToUpper()
            password = password.ToUpper()
            Dim server_offset_public_key = BigNum.fromBytes(server_offset_public_key_bytes)
            Dim client_private_key = BigNum.fromBytes(client_private_key_bytes)
            Dim client_public_key = BigNum.fromBytes(client_public_key_bytes)

            'Password private key
            Dim x = BigNum.fromBytes(SHA1(concat(account_salt, SHA1(concat(packString(username + ":" + password))))))

            'Verifier
            Dim v = G.powerMod(x, N)
            Dim u = BigNum.fromBytes(subArray(SHA1(server_offset_public_key_bytes), 0, 4).reversed)

            'Shared secret
            Dim shared_secret = ((N + server_offset_public_key - v) Mod N).powerMod(client_private_key + u * x, N).toBytes().ToArray()
            ReDim Preserve shared_secret(0 To 31)
            'separate into odd and even bytes
            Dim shared_secret_evens(0 To 15) As Byte
            Dim shared_secret_odds(0 To 15) As Byte
            For i = 0 To 15
                shared_secret_evens(i) = shared_secret(2 * i)
                shared_secret_odds(i) = shared_secret(2 * i + 1)
            Next i
            'hash odds and evens
            shared_secret_evens = SHA1(shared_secret_evens)
            shared_secret_odds = SHA1(shared_secret_odds)
            'interleave hashed odds and evens
            ReDim shared_secret(0 To shared_secret_evens.Length + shared_secret_odds.Length - 1)
            For i = 0 To shared_secret_evens.Length - 1
                shared_secret(2 * i) = shared_secret_evens(i)
                shared_secret(2 * i + 1) = shared_secret_odds(i)
            Next i

            'Fixed salt
            Dim fixed_G = SHA1(G.toBytes().ToArray())
            Dim fixed_N = SHA1(N.toBytes().ToArray())
            Dim fixed_salt(0 To 19) As Byte
            For i = 0 To fixed_G.Length - 1
                fixed_salt(i) = fixed_G(i) Xor fixed_N(i)
            Next i

            'Proofs
            Dim client_proof = SHA1(concat(fixed_salt, _
                                           SHA1(packString(username)), _
                                           account_salt, _
                                           client_public_key_bytes, _
                                           server_offset_public_key_bytes, _
                                           shared_secret))
            Dim server_proof = SHA1(concat(Of Byte)(client_public_key_bytes, client_proof, shared_secret))
            Return New Pair(Of Byte(), Byte())(client_proof, server_proof)
        End Function
#End Region

#Region "MPQ Revision Check"
        Public Function generateRevisionCheck(ByVal folder As String, ByVal mpq_number_string As String, ByVal mpq_hash_challenge As String) As UInteger
            Dim revCheckFiles() As String = { _
                    "War3.exe", _
                    "Storm.dll", _
                    "Game.dll" _
                }
            Dim hashcodes As UInteger() = { _
                    &HE7F4CB62L, _
                    &HF6A14FFCL, _
                    &HAA5504AFL, _
                    &H871FCDC2L, _
                    &H11BF6A18L, _
                    &HC57292E6L, _
                    &H7927D27EL, _
                    &H2FEC8733L _
                }

            Dim vars(0 To 255) As UInteger 'variable values
            Dim opDst() As Integer = Nothing 'destination variable of operation
            Dim opSrc1() As Integer = Nothing 'source variable 1 of operation
            Dim opSrc2() As Integer = Nothing 'source variable 2 of operation
            Dim opFunc() As Char = Nothing 'function applied by operation
            Dim numOps = 0 'number of operations

            'Parse variables and operations
            'Example: [newlines actually spaces in data]
            '   A=443747131
            '   B=3328179921
            '   C=1040998290
            '   4
            '   A=A^S
            '   B=B-C
            '   C=C^A
            '   A=A+B
            Dim lines() = mpq_hash_challenge.Split(" "c)
            For line_index = 0 To lines.Length - 1
                Dim cur_line = lines(line_index).ToUpper()
                If cur_line Like "[A-Z]=#*" Then 'Variable Initialization
                    Dim v0 = Asc(cur_line(0))
                    vars(v0) = UInteger.Parse(cur_line.Substring(2))
                ElseIf Integer.TryParse(cur_line, 0) Then 'Operation Count
                    Dim n = Integer.Parse(cur_line)
                    ReDim opDst(0 To n - 1)
                    ReDim opSrc1(0 To n - 1)
                    ReDim opSrc2(0 To n - 1)
                    ReDim opFunc(0 To n - 1)
                ElseIf cur_line Like "[A-Z]=[A-Z][-+^|&][A-Z]" Then 'Operation
                    If opDst Is Nothing Then Throw New ArgumentException("Missing operation count", "mpq_hash_challenge")
                    If numOps >= opDst.Length Then Throw New ArgumentException("Actual number of operations exceeds operation count", "mpq_hash_challenge")
                    opDst(numOps) = Asc(cur_line(0))
                    opSrc1(numOps) = Asc(cur_line(2))
                    opSrc2(numOps) = Asc(cur_line(4))
                    opFunc(numOps) = cur_line(3)
                    numOps += 1
                End If
            Next line_index

            'Adjust Variable A using mpq number string [the point of this? obfuscation I guess]
            Dim mpq_number As Integer
            mpq_number_string = mpq_number_string.ToLower()
            If Not mpq_number_string Like "ver-ix86-#.mpq" AndAlso Not mpq_number_string Like "ix86ver#.mpq" Then
                Throw New ArgumentException("Unrecognized MPQ String", "mpq_number_string")
            End If
            mpq_number = Asc(mpq_number_string(mpq_number_string.Length - 5)) - Asc("0"c)
            vars(Asc("A"c)) = vars(Asc("A"c)) Xor hashcodes(mpq_number)

            'Tail Buffer [rounds file data sizes to 1024]
            Dim tail_buffer(0 To 1023) As Byte
            For i = 0 To tail_buffer.Length - 1
                tail_buffer(i) = CByte(255 - (i Mod 256))
            Next i
            Dim tail_stream As New IO.MemoryStream(tail_buffer)

            'Parse Files
            For Each filename In revCheckFiles
                'Open file
                Dim f = New IO.FileStream(folder + filename, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                Dim n = modCeiling(CInt(f.Length), 1024)
                Dim br = New IO.BinaryReader(New IO.BufferedStream(New ConcatStream(f, tail_stream)))

                'Apply operations using each dword in stream
                For each_dword = 0 To n - 1 Step 4
                    'Variable S = file dword
                    vars(Asc("S"c)) = br.ReadUInt32()

                    'Run Operations
                    For op = 0 To numOps - 1
                        Dim result As Long
                        Select Case opFunc(op)
                            Case "+"c : result = vars(opSrc1(op)) + CLng(vars(opSrc2(op)))
                            Case "-"c : result = vars(opSrc1(op)) - CLng(vars(opSrc2(op)))
                            Case "^"c : result = vars(opSrc1(op)) Xor vars(opSrc2(op))
                            Case "&"c : result = vars(opSrc1(op)) And vars(opSrc2(op))
                            Case "|"c : result = vars(opSrc1(op)) Or vars(opSrc2(op))
                            Case Else : Throw New ArgumentException("Unrecognized revision check operator: " + opFunc(op), "mpq_hash_challenge")
                        End Select
                        vars(opDst(op)) = uCUInt(result)
                    Next op
                Next each_dword
            Next filename

            'Return value of Variable C
            Return vars(Asc("C"c))
        End Function
#End Region

#Region "SHA1"
        Public Function SHA1(ByVal bb() As Byte) As Byte()
            If Not (bb IsNot Nothing) Then Throw New ArgumentNullException()
            Return New System.Security.Cryptography.SHA1Managed().ComputeHash(bb)
        End Function
#End Region

#Region "Premasked"
        Public Function encodePreMaskedByteArray(ByVal bbDecoded As ReadOnlyArrayView(Of Byte)) As Byte()
            Dim n = bbDecoded.length
            Dim numBlocks = CInt(Math.Ceiling(n / 7))
            Dim bbCoded(0 To n + numBlocks - 1) As Byte

            'For each chunk
            For i = 0 To numBlocks - 1
                Dim d = i * 7 'position in decoded array
                Dim c = i * 8 + 1 'position in coded array
                Dim header = CByte(1) 'placed before the chunk in destination
                Dim p = CByte(&H2) 'bit mask

                'Process chunk
                For j = 0 To 6
                    If d + j >= n Then Exit For
                    bbCoded(c + j) = bbDecoded(d + j) Or CByte(&H1) 'place in coded array with first bit forced to 1
                    If (bbDecoded(d + j) And &H1) = 1 Then header = header Or p 'place first bit in header
                    p <<= 1 'next bit
                Next j

                'place header in destination array
                bbCoded(c - 1) = header
            Next i

            Return bbCoded
        End Function
        Public Function decodePreMaskedByteArray(ByVal bbCoded As ReadOnlyArrayView(Of Byte)) As Byte()
            Dim n = bbCoded.length
            Dim numBlocks = CInt(Math.Ceiling(n / 8))
            Dim bbDecoded(0 To n - numBlocks - 1) As Byte

            'For each chunk
            For i = 0 To numBlocks - 1
                Dim d = i * 7 'position in decoded array
                Dim c = i * 8 + 1 'position in coded array
                Dim header = bbCoded(c - 1)
                Dim p = CByte(&H2) 'bit mask

                'Process chunk
                For j = 0 To 6
                    If c + j >= n Then Exit For
                    bbDecoded(d + j) = bbCoded(c + j) 'place in decoded array
                    If (header And p) = 0 Then bbDecoded(d + j) -= CByte(1) 'unset first bit based on header's bit
                    p <<= 1 'next bit
                Next j
            Next i

            Return bbDecoded
        End Function
#End Region
    End Module

#Region "CDKey"
    Public Class CDKey
#Region "Members"
        Public ReadOnly key As String
        Public ReadOnly productKey As Byte()
        Public ReadOnly privateKey As Byte()
        Public ReadOnly publicKey As Byte()
#End Region

#Region "Shared Members"
        Private Const KEY_LENGTH As Integer = 26
        Private Shared ReadOnly keyMap As Dictionary(Of Char, Byte) = initKeyMap()
        Private Shared ReadOnly invKeyMap As Dictionary(Of Byte, Char) = initInvKeyMap()
        '''<summary>30 permutations of 0-15</summary>
        Private Shared ReadOnly permutationSet As Byte()() = { _
                    New Byte() {&H9, &H4, &H7, &HF, &HD, &HA, &H3, &HB, &H1, &H2, &HC, &H8, &H6, &HE, &H5, &H0}, _
                    New Byte() {&H9, &HB, &H5, &H4, &H8, &HF, &H1, &HE, &H7, &H0, &H3, &H2, &HA, &H6, &HD, &HC}, _
                    New Byte() {&HC, &HE, &H1, &H4, &H9, &HF, &HA, &HB, &HD, &H6, &H0, &H8, &H7, &H2, &H5, &H3}, _
                    New Byte() {&HB, &H2, &H5, &HE, &HD, &H3, &H9, &H0, &H1, &HF, &H7, &HC, &HA, &H6, &H4, &H8}, _
                    New Byte() {&H6, &H2, &H4, &H5, &HB, &H8, &HC, &HE, &HD, &HF, &H7, &H1, &HA, &H0, &H3, &H9}, _
                    New Byte() {&H5, &H4, &HE, &HC, &H7, &H6, &HD, &HA, &HF, &H2, &H9, &H1, &H0, &HB, &H8, &H3}, _
                    New Byte() {&HC, &H7, &H8, &HF, &HB, &H0, &H5, &H9, &HD, &HA, &H6, &HE, &H2, &H4, &H3, &H1}, _
                    New Byte() {&H3, &HA, &HE, &H8, &H1, &HB, &H5, &H4, &H2, &HF, &HD, &HC, &H6, &H7, &H9, &H0}, _
                    New Byte() {&HC, &HD, &H1, &HF, &H8, &HE, &H5, &HB, &H3, &HA, &H9, &H0, &H7, &H2, &H4, &H6}, _
                    New Byte() {&HD, &HA, &H7, &HE, &H1, &H6, &HB, &H8, &HF, &HC, &H5, &H2, &H3, &H0, &H4, &H9}, _
                    New Byte() {&H3, &HE, &H7, &H5, &HB, &HF, &H8, &HC, &H1, &HA, &H4, &HD, &H0, &H6, &H9, &H2}, _
                    New Byte() {&HB, &H6, &H9, &H4, &H1, &H8, &HA, &HD, &H7, &HE, &H0, &HC, &HF, &H2, &H3, &H5}, _
                    New Byte() {&HC, &H7, &H8, &HD, &H3, &HB, &H0, &HE, &H6, &HF, &H9, &H4, &HA, &H1, &H5, &H2}, _
                    New Byte() {&HC, &H6, &HD, &H9, &HB, &H0, &H1, &H2, &HF, &H7, &H3, &H4, &HA, &HE, &H8, &H5}, _
                    New Byte() {&H3, &H6, &H1, &H5, &HB, &HC, &H8, &H0, &HF, &HE, &H9, &H4, &H7, &HA, &HD, &H2}, _
                    New Byte() {&HA, &H7, &HB, &HF, &H2, &H8, &H0, &HD, &HE, &HC, &H1, &H6, &H9, &H3, &H5, &H4}, _
                    New Byte() {&HA, &HB, &HD, &H4, &H3, &H8, &H5, &H9, &H1, &H0, &HF, &HC, &H7, &HE, &H2, &H6}, _
                    New Byte() {&HB, &H4, &HD, &HF, &H1, &H6, &H3, &HE, &H7, &HA, &HC, &H8, &H9, &H2, &H5, &H0}, _
                    New Byte() {&H9, &H6, &H7, &H0, &H1, &HA, &HD, &H2, &H3, &HE, &HF, &HC, &H5, &HB, &H4, &H8}, _
                    New Byte() {&HD, &HE, &H5, &H6, &H1, &H9, &H8, &HC, &H2, &HF, &H3, &H7, &HB, &H4, &H0, &HA}, _
                    New Byte() {&H9, &HF, &H4, &H0, &H1, &H6, &HA, &HE, &H2, &H3, &H7, &HD, &H5, &HB, &H8, &HC}, _
                    New Byte() {&H3, &HE, &H1, &HA, &H2, &HC, &H8, &H4, &HB, &H7, &HD, &H0, &HF, &H6, &H9, &H5}, _
                    New Byte() {&H7, &H2, &HC, &H6, &HA, &H8, &HB, &H0, &HF, &H4, &H3, &HE, &H9, &H1, &HD, &H5}, _
                    New Byte() {&HC, &H4, &H5, &H9, &HA, &H2, &H8, &HD, &H3, &HF, &H1, &HE, &H6, &H7, &HB, &H0}, _
                    New Byte() {&HA, &H8, &HE, &HD, &H9, &HF, &H3, &H0, &H4, &H6, &H1, &HC, &H7, &HB, &H2, &H5}, _
                    New Byte() {&H3, &HC, &H4, &HA, &H2, &HF, &HD, &HE, &H7, &H0, &H5, &H8, &H1, &H6, &HB, &H9}, _
                    New Byte() {&HA, &HC, &H1, &H0, &H9, &HE, &HD, &HB, &H3, &H7, &HF, &H8, &H5, &H2, &H4, &H6}, _
                    New Byte() {&HE, &HA, &H1, &H8, &H7, &H6, &H5, &HC, &H2, &HF, &H0, &HD, &H3, &HB, &H4, &H9}, _
                    New Byte() {&H3, &H8, &HE, &H0, &H7, &H9, &HF, &HC, &H1, &H6, &HD, &H2, &H5, &HA, &HB, &H4}, _
                    New Byte() {&H3, &HA, &HC, &H4, &HD, &HB, &H9, &HE, &HF, &H6, &H1, &H7, &H2, &H0, &H5, &H8} _
                }
        Private Shared ReadOnly invPermutationSet As Byte()() = initInvPermMap()

        Private Shared Function initKeyMap() As Dictionary(Of Char, Byte)
            Dim vals As New Dictionary(Of Char, Byte)
            Dim chars As String = "246789BCDEFGHJKMNPRTVWXYZ"
            For b As Byte = 0 To CByte(chars.Length - 1)
                vals(chars(b)) = b
            Next b
            Return vals
        End Function
        Private Shared Function initInvKeyMap() As Dictionary(Of Byte, Char)
            If keyMap Is Nothing Then Throw New InvalidOperationException("Key map must be initialized before inverse key map")
            Dim vals As New Dictionary(Of Byte, Char)
            For Each e As Char In keyMap.Keys
                vals(keyMap(e)) = e
            Next e
            Return vals
        End Function
        Private Shared Function initInvPermMap() As Byte()()
            Dim invPermSet(0 To 29)() As Byte
            For i As Integer = 0 To 29
                ReDim invPermSet(i)(0 To 15)
                For j As Byte = 0 To 15
                    invPermSet(i)(permutationSet(i)(j)) = j
                Next j
            Next i
            Return invPermSet
        End Function
#End Region

#Region "Decode"
        Public Sub New(ByVal key As String)
            Dim cdkey As Char() = key.ToUpper().Replace("-", "").Replace(" ", "").ToCharArray()
            If cdkey.Length <> KEY_LENGTH Then Throw New ArgumentException("Invalid cd key length.")

            'Shuffle the cd key into the digits of a base 5 number
            Dim d = 33
            Dim n_digitsBase5(0 To KEY_LENGTH * 2 - 1) As Byte
            For i = 0 To KEY_LENGTH - 1
                If Not keyMap.ContainsKey(cdkey(i)) Then Throw New ArgumentException("Invalid cd key character: " + cdkey(i))
                Dim c = keyMap(cdkey(i)) '[range: 0 to 24]

                'extract the two base 5 digits of c
                d = (d + 49) Mod n_digitsBase5.Length
                n_digitsBase5(d) = CByte(c \ 5)
                d = (d + 49) Mod n_digitsBase5.Length
                n_digitsBase5(d) = CByte(c Mod 5)
            Next i

            'Permute nibbles
            Dim n_digitsBase16 = BigNum.fromBaseBytes(n_digitsBase5, 5).toBaseBytes(16).ToArray()
            ReDim Preserve n_digitsBase16(0 To 31)
            For r = 29 To 0 Step -1
                Dim perm() As Byte = permutationSet(r)
                Dim c As Byte = n_digitsBase16(r)

                'Permute
                For r2 = 29 To 0 Step -1
                    If r = r2 Then Continue For
                    c = perm(n_digitsBase16(r2) Xor perm(c))
                Next r2

                n_digitsBase16(r) = perm(c)
            Next r

            'Swap bits
            Dim n_digitsBase2 = BigNum.fromBaseBytes(n_digitsBase16, 16).toBaseBytes(2).ToArray()
            ReDim Preserve n_digitsBase2(0 To 127)
            swapBits11Mod120(n_digitsBase2)

            'Extract keys
            Dim n_digitsBase256 = BigNum.fromBaseBytes(n_digitsBase2, 2).toBytes().ToArray()
            ReDim Preserve n_digitsBase256(0 To 15)
            Me.productKey = New Byte() {n_digitsBase256(13) >> &H2, 0, 0, 0}
            Me.publicKey = New Byte() {n_digitsBase256(10), n_digitsBase256(11), n_digitsBase256(12), 0}
            Me.privateKey = New Byte() {n_digitsBase256(8), n_digitsBase256(9), n_digitsBase256(4), n_digitsBase256(5), n_digitsBase256(6), n_digitsBase256(7), n_digitsBase256(0), n_digitsBase256(1), n_digitsBase256(2), n_digitsBase256(3)}
        End Sub
#End Region

#Region "Encode"
        Public Sub New(ByVal productKey As Byte(), ByVal publicKey As Byte(), ByVal privateKey As Byte())
            'Inject keys
            Dim n_digitsBase256 = New Byte() { _
                        privateKey(6), privateKey(7), privateKey(8), privateKey(9), _
                        privateKey(2), privateKey(3), privateKey(4), privateKey(5), _
                        privateKey(0), privateKey(1), _
                        publicKey(0), publicKey(1), publicKey(2), _
                        productKey(0) << &H2, _
                        0, 0}

            'Swap bits
            Dim n_digitsBase2 = BigNum.fromBaseBytes(n_digitsBase256, 256).toBaseBytes(2).ToArray()
            ReDim Preserve n_digitsBase2(0 To 127)
            swapBits11Mod120(n_digitsBase2)

            'Unpermute nibbles
            Dim n_digitsBase16 = BigNum.fromBaseBytes(n_digitsBase2, 2).toBaseBytes(16).ToArray()
            ReDim Preserve n_digitsBase16(0 To 31)
            For r = 0 To 29
                Dim unperm() As Byte = invPermutationSet(r)
                Dim c As Byte = unperm(n_digitsBase16(r))

                'Unpermute
                For r2 = 0 To 29
                    If r = r2 Then Continue For
                    c = unperm(n_digitsBase16(r2) Xor unperm(c))
                Next r2

                n_digitsBase16(r) = c
            Next r

            'Shuffle the base 5 digits into the cd key
            Dim n_digitsBase5 = BigNum.fromBaseBytes(n_digitsBase16, 16).toBaseBytes(5).ToArray()
            ReDim Preserve n_digitsBase5(0 To KEY_LENGTH * 2 - 1)
            Dim d = 33
            Dim cdkey = ""
            For i = 0 To KEY_LENGTH - 1
                Dim c As Byte

                'combine two digits to get base 25 digit
                d = (d + 49) Mod n_digitsBase5.Length
                c = n_digitsBase5(d)
                d = (d + 49) Mod n_digitsBase5.Length
                c = c * CByte(5) + n_digitsBase5(d)

                cdkey += invKeyMap(c)
            Next i

            key = cdkey.ToUpper()
        End Sub
#End Region

#Region "Misc"
        '''<summary>Swap the bits with their *11 counterpart mod 120</summary>
        '''<remarks>This transformation is its own inverse: x*11*11 = x*121 = x*1 = x (mod 120)</remarks>
        Private Sub swapBits11Mod120(ByVal bits() As Byte)
            If Not (bits IsNot Nothing) Then Throw New ArgumentNullException()
            If bits.Length <> 128 Then Throw New ArgumentException("bits", "Size must be 128")

            For i As Integer = 0 To 119
                Dim j As Integer = (i * 11) Mod 120
                If j <= i Then Continue For 'don't swap the same bits twice

                'swap bits i and j
                Dim b As Byte = bits(i)
                bits(i) = bits(j)
                bits(j) = b
            Next i
        End Sub
#End Region
    End Class
#End Region
End Namespace
