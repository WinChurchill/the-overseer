// Installation ownership policy. Never recursively delete the application tree.
var
  PreviousOwnedFiles: TArrayOfString;

function OwnershipFileAttributes(Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function IsOwnStartupCommand(Command, Executable: String): Boolean;
var
  Token: String;
  I: Integer;
begin
  Result := False;
  Command := Trim(Command);
  if Command = '' then Exit;
  if Command[1] = '"' then
  begin
    I := 2;
    while (I <= Length(Command)) and (Command[I] <> '"') do I := I + 1;
    if I > Length(Command) then Exit;
    Token := Copy(Command, 2, I - 2);
    if I < Length(Command) then
      if (Command[I + 1] <> ' ') and (Command[I + 1] <> #9) then Exit;
  end
  else
  begin
    I := 1;
    while (I <= Length(Command)) and (Command[I] <> ' ') and (Command[I] <> #9) do I := I + 1;
    Token := Copy(Command, 1, I - 1);
  end;
  StringChangeEx(Token, '/', '\', True);
  if ExtractFileDrive(Token) = '' then Exit;
  Result := CompareText(ExpandFileName(Token), ExpandFileName(Executable)) = 0;
end;

function LoadOwnershipManifest(Name: String; var Lines: TArrayOfString): Boolean;
var
  Rec: TFindRec;
begin
  Result := False;
  SetArrayLength(Lines, 0);
  if not FindFirst(Name, Rec) then Exit;
  try
    if ((Rec.Attributes and $400) <> 0) or (Rec.SizeHigh <> 0) or (Rec.SizeLow > 2097152) then Exit;
  finally
    FindClose(Rec);
  end;
  if not LoadStringsFromFile(Name, Lines) then
  begin
    SetArrayLength(Lines, 0);
    Exit;
  end;
  if (GetArrayLength(Lines) = 0) or (GetArrayLength(Lines) > 4096) then
  begin
    SetArrayLength(Lines, 0);
    Exit;
  end;
  Result := True;
end;

function ParseOwnedFile(Line: String; var Digest, Relative: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := False;
  if (Length(Line) < 66) or (Length(Line) > 1100) or (Pos(#9, Line) <> 65) then Exit;
  Digest := Lowercase(Copy(Line, 1, 64));
  for I := 1 to Length(Digest) do
  begin
    C := Digest[I];
    if ((C < '0') or (C > '9')) and ((C < 'a') or (C > 'f')) then Exit;
  end;
  Relative := Copy(Line, 66, Length(Line));
  StringChangeEx(Relative, '/', '\', True);
  if (Relative = '') or (Relative[1] = '\') or (Pos(':', Relative) > 0) then Exit;
  if (Pos('\..\', '\' + Relative + '\') > 0) or (Pos('\.\', '\' + Relative + '\') > 0) or
     (Pos('\\', Relative) > 0) or (Pos('*', Relative) > 0) or (Pos('?', Relative) > 0) then Exit;
  // Win32 trims trailing dots/spaces; reject aliases such as ".. " before I/O.
  if (Relative[Length(Relative)] = '.') or (Relative[Length(Relative)] = ' ') then Exit;
  for I := 1 to Length(Relative) do
  begin
    C := Relative[I];
    if (C < #32) or (C = '"') or (C = '<') or (C = '>') or (C = '|') then Exit;
    if C = '\' then
      if (Relative[I - 1] = '.') or (Relative[I - 1] = ' ') then Exit;
  end;
  Result := True;
end;

function IsContainedRegularFile(Name, Root: String): Boolean;
var
  Current: String;
  Attributes: LongWord;
begin
  Result := False;
  Root := RemoveBackslash(ExpandFileName(Root));
  Current := ExpandFileName(Name);
  if CompareText(Copy(Current, 1, Length(Root) + 1), AddBackslash(Root)) <> 0 then Exit;
  // Reject a reparse point anywhere between the file and the installation root.
  while True do
  begin
    Attributes := OwnershipFileAttributes(Current);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then Exit;
    if CompareText(Current, Root) = 0 then Break;
    Current := ExtractFileDir(Current);
    if Length(Current) < Length(Root) then Exit;
  end;
  Result := True;
end;

procedure RemoveObsoleteOwnedFiles;
var
  CurrentOwnedFiles: TArrayOfString;
  I, J: Integer;
  Digest, Relative, NewDigest, NewRelative, Target, Parent, Root: String;
  StillOwned: Boolean;
begin
  if GetArrayLength(PreviousOwnedFiles) = 0 then Exit;
  Root := RemoveBackslash(ExpandConstant('{app}'));
  if not LoadOwnershipManifest(AddBackslash(Root) + 'overseer-installed-files.txt', CurrentOwnedFiles) then Exit;
  // A damaged new inventory must never make current payload files look obsolete.
  for J := 0 to GetArrayLength(CurrentOwnedFiles) - 1 do
    if not ParseOwnedFile(CurrentOwnedFiles[J], NewDigest, NewRelative) then Exit;
  for I := 0 to GetArrayLength(PreviousOwnedFiles) - 1 do
  begin
    if not ParseOwnedFile(PreviousOwnedFiles[I], Digest, Relative) then Continue;
    StillOwned := False;
    for J := 0 to GetArrayLength(CurrentOwnedFiles) - 1 do
    begin
      ParseOwnedFile(CurrentOwnedFiles[J], NewDigest, NewRelative);
      if CompareText(Relative, NewRelative) = 0 then
      begin
        StillOwned := True;
        Break;
      end;
    end;
    if StillOwned then Continue;
    Target := AddBackslash(Root) + Relative;
    if not IsContainedRegularFile(Target, Root) then Continue;
    try
      if CompareText(GetSHA256OfFile(Target), Digest) <> 0 then Continue;
      if not DeleteFile(Target) then Continue;
      // Only prune empty parent directories; never touch the root or user content.
      Parent := ExtractFileDir(Target);
      while (Length(Parent) > Length(Root)) and (CompareText(Copy(Parent, 1, Length(Root) + 1), AddBackslash(Root)) = 0) do
      begin
        if not RemoveDir(Parent) then Break;
        Parent := ExtractFileDir(Parent);
      end;
    except
      Log('Preserving obsolete file whose ownership could not be verified.');
    end;
  end;
end;
